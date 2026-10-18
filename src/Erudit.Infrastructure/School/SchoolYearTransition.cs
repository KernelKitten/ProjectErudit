using Erudit.Domain.School;
using Erudit.Infrastructure.Persistence;
using Microsoft.EntityFrameworkCore;

namespace Erudit.Infrastructure.School;

/// <summary>
/// Перевод всей школы на следующий учебный год (согласовано 2026-10-18):
///  - создаётся следующий учебный год (1 сентября — 31 августа), если его нет;
///  - класс N «буква» → класс N+1 той же буквы, ученики переходят: в старом классе до 31 августа, в новом — с 1 сентября;
///  - в 5–9 классах только буквы «а» и «б»: 4-й класс с другой буквой (4в) делится между 5а и 5б —
///    кого куда, решает завуч в окне перевода (по умолчанию поровну);
///  - все 9-е классы объединяются в один 10-й («10»); его руководитель — руководитель 9-го, если 9-й был один,
///    иначе назначают вручную;
///  - классный руководитель идёт с классом, КРОМЕ 4 → 5: у 5-х руководителя назначают вручную,
///    а учитель бывшего 4-го класса получает новый пустой 1-й класс той же буквы (учеников туда загрузят);
///  - 11-е классы — выпускники: состав закрывается, ученики выключаются (остаются в базе для отчётов);
///  - второго года в школе нет: не сдавших экзамены отмечают «Выбыл» (до или после перевода);
///  - повторно перевести нельзя: если в следующем году уже есть классы, перевод заблокирован.
/// </summary>
public sealed class SchoolYearTransition(EruditDbContext db)
{
    /// <summary>Буква объединённого 10-го класса: пустая — класс называется просто «10».</summary>
    public const string TenthLetter = "";

    /// <summary>
    /// В какой класс переходит класс «параллель + буква». Обычно — на параллель старше с той же буквой,
    /// но все 9-е (9а, 9б...) объединяются в один 10-й класс.
    /// </summary>
    public static (int Grade, string Letter) TargetOf(int grade, string letter) =>
        grade == 9 ? (10, TenthLetter) : (grade + 1, letter);

    /// <summary>Буквы классов 5–9 параллелей. 4-й класс с другой буквой делится между ними.</summary>
    public static readonly IReadOnlyList<string> MiddleLetters = ["а", "б"];

    private static bool IsSplit(int grade, string letter) => grade == 4 && !MiddleLetters.Contains(letter);

    public async Task<TransitionPlan> PreviewAsync(int fromYearId, CancellationToken ct = default)
    {
        var from = await db.AcademicYears.SingleAsync(y => y.Id == fromYearId, ct);
        var nextStart = from.EndDate.AddDays(1);
        var next = await db.AcademicYears.SingleOrDefaultAsync(y => y.StartDate == nextStart, ct);
        var nextName = next?.Name ?? $"{nextStart.Year}/{(nextStart.Year + 1) % 100:D2}";

        var classes = await db.SchoolClasses
            .Where(c => c.AcademicYearId == fromYearId)
            .OrderBy(c => c.Grade).ThenBy(c => c.Letter)
            .Select(c => new
            {
                c.Id,
                c.Grade,
                c.Letter,
                c.ClassTeacherId,
                Teacher = c.ClassTeacher == null ? null : c.ClassTeacher.LastName + " " + c.ClassTeacher.FirstName.Substring(0, 1) + ".",
                Students = db.ClassMemberships.Count(m => m.SchoolClassId == c.Id && m.ToDate == null),
            })
            .ToListAsync(ct);

        // Новые классы: ключ — (параллель, буква). Новый 1-й класс может получиться и из 0-го (с учениками),
        // и из 4-го (руководитель) — тогда объединяем: ученики из 0-го, руководитель из 4-го.
        var targets = new Dictionary<(int Grade, string Letter), NewClass>();
        NewClass Target(int grade, string letter) =>
            targets.TryGetValue((grade, letter), out var t) ? t : targets[(grade, letter)] = new NewClass(grade, letter);

        var graduates = 0;
        foreach (var c in classes)
        {
            if (c.Grade == 11)
            {
                graduates += c.Students;
                continue;
            }

            if (IsSplit(c.Grade, c.Letter))
            {
                // 4в делится между 5а и 5б (ученики — ниже, в SplitStudents); руководитель — в новый 1в.
                foreach (var l in MiddleLetters)
                {
                    Target(5, l).FromClasses.Add($"{c.Grade}{c.Letter} (часть)");
                }
                var first = Target(1, c.Letter);
                first.ClassTeacherId = c.ClassTeacherId;
                first.ClassTeacher = c.Teacher;
                continue;
            }

            var (grade, letter) = TargetOf(c.Grade, c.Letter);
            var target = Target(grade, letter);
            target.FromClasses.Add($"{c.Grade}{c.Letter}");
            target.Students += c.Students;
            if (c.Grade == 9)
            {
                // Все 9-е сливаются в один 10-й. Руководитель — бывший руководитель 9-го, только если 9-й был один;
                // если 9-х несколько — назначают вручную (ниже).
                continue;
            }
            if (c.Grade == 4)
            {
                // Учитель 4-го класса начинает новый 1-й класс той же буквы.
                var firstGrade = Target(1, c.Letter);
                firstGrade.ClassTeacherId = c.ClassTeacherId;
                firstGrade.ClassTeacher = c.Teacher;
            }
            else if (target.Grade != 5 && target.ClassTeacherId is null
                     && !(target.Grade == 1 && classes.Any(x => x.Grade == 4 && x.Letter == c.Letter)))
            {
                // Руководитель идёт с классом. Исключения: у 5-х — вручную (выше их пропускаем),
                // 1-й класс, если есть 4-й той же буквы, — руководитель из 4-го.
                target.ClassTeacherId = c.ClassTeacherId;
                target.ClassTeacher = c.Teacher;
            }
        }

        var ninth = classes.Where(c => c.Grade == 9).ToList();
        if (ninth.Count == 1)
        {
            var tenth = Target(10, TenthLetter);
            tenth.ClassTeacherId = ninth[0].ClassTeacherId;
            tenth.ClassTeacher = ninth[0].Teacher;
        }

        // Ученики делящихся 4-х классов: по алфавиту, каждый — в тот из 5а/5б, где сейчас меньше учеников.
        var splitIds = classes.Where(c => IsSplit(c.Grade, c.Letter)).Select(c => c.Id).ToList();
        var splitRows = await db.ClassMemberships
            .Where(m => splitIds.Contains(m.SchoolClassId) && m.ToDate == null)
            .OrderBy(m => m.Student.LastName).ThenBy(m => m.Student.FirstName)
            .Select(m => new { m.StudentId, m.Student.LastName, m.Student.FirstName, m.SchoolClass.Grade, m.SchoolClass.Letter })
            .ToListAsync(ct);
        var counts = MiddleLetters.ToDictionary(l => l, l => targets.TryGetValue((5, l), out var t) ? t.Students : 0);
        var split = new List<SplitStudent>();
        foreach (var r in splitRows)
        {
            var suggested = MiddleLetters.MinBy(l => counts[l])!;
            counts[suggested]++;
            split.Add(new SplitStudent(r.StudentId, $"{r.LastName} {r.FirstName}", $"{r.Grade}{r.Letter}", suggested));
        }

        string? blocked = null;
        if (classes.Count == 0)
        {
            blocked = $"В учебном году {from.Name} нет классов — переводить некого.";
        }
        else if (next is not null && await db.SchoolClasses.AnyAsync(c => c.AcademicYearId == next.Id, ct))
        {
            blocked = $"В учебном году {next.Name} уже есть классы — перевод уже сделан.";
        }

        return new TransitionPlan(from.Name, nextName, nextStart,
            targets.Values.OrderBy(t => t.Grade).ThenBy(t => t.Letter).ToList(), split, graduates, blocked);
    }

    /// <summary>
    /// Выполняет перевод одной транзакцией. <paramref name="split"/> — куда идёт каждый ученик делящегося 4-го класса
    /// (id ученика → буква 5-го класса); нужен, только если такие ученики есть.
    /// Возвращает план (что сделано) или ошибку, если перевод заблокирован или распределение неполное.
    /// </summary>
    public async Task<TransitionPlan> ApplyAsync(int fromYearId, IReadOnlyDictionary<int, string>? split = null,
        CancellationToken ct = default)
    {
        var plan = await PreviewAsync(fromYearId, ct);
        if (plan.Blocked is not null)
        {
            throw new InvalidOperationException(plan.Blocked);
        }
        split ??= new Dictionary<int, string>();
        var notSplit = plan.SplitStudents
            .Where(s => !split.TryGetValue(s.StudentId, out var l) || !MiddleLetters.Contains(l))
            .ToList();
        if (notSplit.Count > 0)
        {
            throw new InvalidOperationException(
                "Не распределены по 5-м классам: " + string.Join(", ", notSplit.Select(s => s.Name)) + ". Откройте перевод заново.");
        }

        var from = await db.AcademicYears.SingleAsync(y => y.Id == fromYearId, ct);
        var next = await db.AcademicYears.SingleOrDefaultAsync(y => y.StartDate == plan.NextStart, ct)
                   ?? db.AcademicYears.Add(new AcademicYear
                   {
                       Name = plan.NextYearName,
                       StartDate = plan.NextStart,
                       EndDate = plan.NextStart.AddYears(1).AddDays(-1),
                   }).Entity;

        var newClasses = plan.NewClasses.ToDictionary(
            t => (t.Grade, t.Letter),
            t => db.SchoolClasses.Add(new SchoolClass
            {
                AcademicYear = next,
                Grade = t.Grade,
                Letter = t.Letter,
                ClassTeacherId = t.ClassTeacherId,
            }).Entity);

        var memberships = await db.ClassMemberships
            .Include(m => m.SchoolClass)
            .Include(m => m.Student)
            .Where(m => m.SchoolClass.AcademicYearId == fromYearId && m.ToDate == null)
            .ToListAsync(ct);

        foreach (var m in memberships)
        {
            m.ToDate = from.EndDate;
            if (m.SchoolClass.Grade == 11)
            {
                m.Student.IsActive = false; // выпускник
                continue;
            }
            var target = IsSplit(m.SchoolClass.Grade, m.SchoolClass.Letter)
                ? (5, split[m.StudentId])
                : TargetOf(m.SchoolClass.Grade, m.SchoolClass.Letter);
            db.ClassMemberships.Add(new ClassMembership
            {
                StudentId = m.StudentId,
                SchoolClass = newClasses[target],
                FromDate = next.StartDate,
            });
        }

        await db.SaveChangesAsync(ct); // одна транзакция — всё или ничего
        return plan;
    }
}

public sealed record TransitionPlan(string FromYearName, string NextYearName, DateOnly NextStart,
    IReadOnlyList<NewClass> NewClasses, IReadOnlyList<SplitStudent> SplitStudents, int Graduates, string? Blocked);

/// <summary>Ученик делящегося 4-го класса (4в) и предложенная буква 5-го класса.</summary>
public sealed record SplitStudent(int StudentId, string Name, string FromClass, string SuggestedLetter);

public sealed class NewClass(int grade, string letter)
{
    public int Grade { get; } = grade;
    public string Letter { get; } = letter;
    public string Name => $"{Grade}{Letter}";
    public List<string> FromClasses { get; } = [];
    /// <summary>Учеников без учёта делящегося 4-го класса (их распределяют в окне перевода).</summary>
    public int Students { get; set; }
    public int? ClassTeacherId { get; set; }
    public string? ClassTeacher { get; set; }
}
