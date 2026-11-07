namespace Erudit.Domain.Lessons;

/// <summary>Академические часы: в отчётах время считается по 40 минут. В базе храним минуты.</summary>
public static class AcademicHours
{
    public const int MinutesPerHour = 40;

    /// <summary>Округляем только итог, до сотых (точность уточнит руководитель): передавайте сумму минут, а не складывайте часы.</summary>
    public static decimal FromMinutes(int minutes) =>
        Math.Round(minutes / (decimal)MinutesPerHour, 2, MidpointRounding.AwayFromZero);
}

public enum ReportLessonKind
{
    NoStudents = 0, // проведено, но никто не записан
    Individual = 1, // один записанный ученик
    Group = 2,      // два и больше
}

/// <summary>Записанный ученик в занятии отчёта. ClassName — класс на дату занятия.</summary>
public sealed record ReportStudent(int Id, string Name, string? ClassName, Attendance Attendance);

/// <param name="Ended">Занятие уже закончилось (важно для запланированных: значит, его забыли отметить).</param>
/// <param name="Minutes">Минуты для отчёта: фактические, если учитель их указал, иначе по плану.</param>
/// <param name="Students">Только действующие записи: отказавшиеся и убранные в отчёт не входят.</param>
public sealed record ReportLesson(
    int Id,
    DateOnly Date,
    TimeOnly Start,
    int? LessonNumber,
    string Subject,
    string Room,
    LessonStatus Status,
    bool Ended,
    int Minutes,
    IReadOnlyList<ReportStudent> Students)
{
    /// <summary>По числу записанных, а не пришедших: занятие на троих с одним пришедшим остаётся групповым.</summary>
    public ReportLessonKind Kind => Students.Count switch
    {
        0 => ReportLessonKind.NoStudents,
        1 => ReportLessonKind.Individual,
        _ => ReportLessonKind.Group,
    };

    public bool IsUnmarked => Status == LessonStatus.Planned && Ended;
}

public sealed record ReportKindTotal(ReportLessonKind Kind, int Lessons, int Minutes)
{
    public decimal Hours => AcademicHours.FromMinutes(Minutes);
}

/// <summary>Ученик за период. Minutes — только занятия, на которых он был. Перешёл в другой класс — две строки.</summary>
public sealed record ReportStudentRow(int Id, string Name, string? ClassName, int Attended, int Absent, int Minutes)
{
    public decimal Hours => AcademicHours.FromMinutes(Minutes);
}

public sealed record ReportClassRow(string? ClassName, int Students, int Attended, int Absent, int Minutes)
{
    public decimal Hours => AcademicHours.FromMinutes(Minutes);
}

/// <summary>
/// Учитель в сводке завуча. Часы «Всего» включают и проведённые занятия без учеников,
/// поэтому могут быть больше суммы индивидуальных и групповых.
/// </summary>
public sealed record TeacherSummaryRow(int TeacherId, string Name, bool IsActive,
    int Held, int NotHeld, int Cancelled, int Unmarked, int IndividualMinutes, int GroupMinutes, int TotalMinutes)
{
    public decimal IndividualHours => AcademicHours.FromMinutes(IndividualMinutes);
    public decimal GroupHours => AcademicHours.FromMinutes(GroupMinutes);
    public decimal TotalHours => AcademicHours.FromMinutes(TotalMinutes);

    /// <summary>Итоговая строка: минуты складываем, часы считаем из суммы минут (округляется только итог).</summary>
    public static TeacherSummaryRow Total(IReadOnlyCollection<TeacherSummaryRow> rows) => new(0, "Итого", true,
        rows.Sum(r => r.Held), rows.Sum(r => r.NotHeld), rows.Sum(r => r.Cancelled), rows.Sum(r => r.Unmarked),
        rows.Sum(r => r.IndividualMinutes), rows.Sum(r => r.GroupMinutes), rows.Sum(r => r.TotalMinutes));
}

/// <summary>
/// Отчёт учителя за период. Чистая логика без базы. В часы идут только проведённые занятия:
/// групповое занятие на 1 час с 5 учениками — у учителя 1 час и у каждого пришедшего ученика 1 час.
/// </summary>
public sealed class TeacherReport
{
    /// <summary>Проведённые, «не состоялось» и неотмеченные, по порядку. Отменённые и будущие — только в счётчиках.</summary>
    public required IReadOnlyList<ReportLesson> Lessons { get; init; }
    /// <summary>Часы проведённых занятий по видам; вид без занятий не показываем.</summary>
    public required IReadOnlyList<ReportKindTotal> Kinds { get; init; }
    public required IReadOnlyList<ReportStudentRow> Students { get; init; }
    public required IReadOnlyList<ReportClassRow> Classes { get; init; }

    public required int Held { get; init; }
    public required int NotHeld { get; init; }
    public required int Unmarked { get; init; }
    public required int Cancelled { get; init; }
    public required int Upcoming { get; init; }
    public required int TotalMinutes { get; init; }

    public decimal TotalHours => AcademicHours.FromMinutes(TotalMinutes);

    public static TeacherReport Build(IEnumerable<ReportLesson> source)
    {
        var all = source.OrderBy(l => l.Date).ThenBy(l => l.Start).ToList();
        var held = all.Where(l => l.Status == LessonStatus.Held).ToList();

        var kinds = held.GroupBy(l => l.Kind)
            .OrderByDescending(g => g.Key == ReportLessonKind.Individual).ThenByDescending(g => g.Key)
            .Select(g => new ReportKindTotal(g.Key, g.Count(), g.Sum(l => l.Minutes)))
            .ToList();

        var students = held
            .SelectMany(l => l.Students.Select(s => (Lesson: l, Student: s)))
            .GroupBy(x => (x.Student.Id, x.Student.Name, x.Student.ClassName))
            .Select(g => new ReportStudentRow(g.Key.Id, g.Key.Name, g.Key.ClassName,
                g.Count(x => x.Student.Attendance == Attendance.Present),
                g.Count(x => x.Student.Attendance == Attendance.Absent),
                g.Where(x => x.Student.Attendance == Attendance.Present).Sum(x => x.Lesson.Minutes)))
            .OrderBy(s => s.Name, StringComparer.Create(System.Globalization.CultureInfo.GetCultureInfo("ru-RU"), false))
            .ToList();

        var classes = students.GroupBy(s => s.ClassName)
            .Select(g => new ReportClassRow(g.Key, g.Select(s => s.Id).Distinct().Count(),
                g.Sum(s => s.Attended), g.Sum(s => s.Absent), g.Sum(s => s.Minutes)))
            .OrderBy(c => c.ClassName is null).ThenBy(c => ClassSortKey(c.ClassName))
            .ToList();

        return new TeacherReport
        {
            Lessons = all.Where(l => l.Status is LessonStatus.Held or LessonStatus.NotHeld || l.IsUnmarked).ToList(),
            Kinds = kinds,
            Students = students,
            Classes = classes,
            Held = held.Count,
            NotHeld = all.Count(l => l.Status == LessonStatus.NotHeld),
            Unmarked = all.Count(l => l.IsUnmarked),
            Cancelled = all.Count(l => l.Status == LessonStatus.Cancelled),
            Upcoming = all.Count(l => l.Status == LessonStatus.Planned && !l.Ended),
            TotalMinutes = held.Sum(l => l.Minutes),
        };
    }

    /// <summary>Строка сводки завуча: итоги этого отчёта одной строкой.</summary>
    public TeacherSummaryRow ToSummary(int teacherId, string name, bool isActive) => new(teacherId, name, isActive,
        Held, NotHeld, Cancelled, Unmarked,
        Kinds.Where(k => k.Kind == ReportLessonKind.Individual).Sum(k => k.Minutes),
        Kinds.Where(k => k.Kind == ReportLessonKind.Group).Sum(k => k.Minutes),
        TotalMinutes);

    /// <summary>«9а» раньше «10»: дополняем номер нулями, чтобы сортировка строк шла как по числам.</summary>
    private static string ClassSortKey(string? name) => (name ?? "").PadLeft(name is { Length: > 0 } && char.IsDigit(name[^1]) ? 2 : 3, '0');
}
