using Erudit.Domain.School;
using Erudit.Infrastructure.Persistence;
using Microsoft.EntityFrameworkCore;

namespace Erudit.Infrastructure.School;

/// <summary>
/// Перевод всей школы на следующий учебный год (согласовано 2026-10-18):
///  - создаётся следующий учебный год (1 сентября — 31 августа), если его нет;
///  - класс N «буква» → класс N+1 той же буквы, ученики переходят: в старом классе до 31 августа, в новом — с 1 сентября;
///  - классный руководитель идёт с классом, КРОМЕ 4 → 5: у 5-х руководителя назначают вручную,
///    а учитель бывшего 4-го класса получает новый пустой 1-й класс той же буквы (учеников туда загрузят);
///  - 11-е классы — выпускники: состав закрывается, ученики выключаются (остаются в базе для отчётов);
///  - второго года в школе нет: не сдавших экзамены отмечают «Выбыл» (до или после перевода);
///  - повторно перевести нельзя: если в следующем году уже есть классы, перевод заблокирован.
/// </summary>
public sealed class SchoolYearTransition(EruditDbContext db)
{
    public async Task<TransitionPlan> PreviewAsync(int fromYearId, CancellationToken ct = default)
    {
        var from = await db.AcademicYears.SingleAsync(y => y.Id == fromYearId, ct);
        var nextStart = from.EndDate.AddDays(1);
        var next = await db.AcademicYears.SingleOrDefaultAsync(y => y.StartDate == nextStart, ct);
        var nextName = next?.Name ?? $"{nextStart.Year}/{(nextStart.Year + 1) % 100:D2}";

        var classes = await db.SchoolClasses
            .Where(c => c.AcademicYearId == fromYearId)
            .Select(c => new
            {
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

            var target = Target(c.Grade + 1, c.Letter);
            target.FromClasses.Add($"{c.Grade}{c.Letter}");
            target.Students += c.Students;
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
            targets.Values.OrderBy(t => t.Grade).ThenBy(t => t.Letter).ToList(), graduates, blocked);
    }

    /// <summary>Выполняет перевод одной транзакцией. Возвращает план (что сделано) или ошибку, если перевод заблокирован.</summary>
    public async Task<TransitionPlan> ApplyAsync(int fromYearId, CancellationToken ct = default)
    {
        var plan = await PreviewAsync(fromYearId, ct);
        if (plan.Blocked is not null)
        {
            throw new InvalidOperationException(plan.Blocked);
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
            db.ClassMemberships.Add(new ClassMembership
            {
                StudentId = m.StudentId,
                SchoolClass = newClasses[(m.SchoolClass.Grade + 1, m.SchoolClass.Letter)],
                FromDate = next.StartDate,
            });
        }

        await db.SaveChangesAsync(ct); // одна транзакция — всё или ничего
        return plan;
    }
}

public sealed record TransitionPlan(string FromYearName, string NextYearName, DateOnly NextStart,
    IReadOnlyList<NewClass> NewClasses, int Graduates, string? Blocked);

public sealed class NewClass(int grade, string letter)
{
    public int Grade { get; } = grade;
    public string Letter { get; } = letter;
    public string Name => $"{Grade}{Letter}";
    public List<string> FromClasses { get; } = [];
    public int Students { get; set; }
    public int? ClassTeacherId { get; set; }
    public string? ClassTeacher { get; set; }
}
