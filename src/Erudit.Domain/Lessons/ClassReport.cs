using System.Globalization;

namespace Erudit.Domain.Lessons;

/// <summary>Ученик класса в отчёте. Note — «до 30.09» / «с 01.10», если был в классе не весь период.</summary>
public sealed record ClassReportStudent(int Id, string Name, string? Note);

/// <summary>Запись ученика на занятие (только действующая и только пока он был в этом классе).</summary>
/// <param name="Minutes">Фактические минуты, если учитель их указал, иначе по плану.</param>
public sealed record ClassReportVisit(int StudentId, string Subject, string Teacher, LessonStatus Status, bool Ended,
    Attendance Attendance, int Minutes);

/// <summary>Счётчики: проведено (был записан), был, не был, впереди; минуты — только где был.</summary>
public sealed record ClassReportCounts(int Held, int Attended, int Absent, int Upcoming, int Minutes)
{
    public decimal Hours => AcademicHours.FromMinutes(Minutes);

    public static ClassReportCounts From(IEnumerable<ClassReportVisit> visits)
    {
        var list = visits.ToList();
        var held = list.Where(v => v.Status == LessonStatus.Held).ToList();
        return new ClassReportCounts(held.Count,
            held.Count(v => v.Attendance == Attendance.Present),
            held.Count(v => v.Attendance == Attendance.Absent),
            list.Count(v => v.Status == LessonStatus.Planned && !v.Ended),
            held.Where(v => v.Attendance == Attendance.Present).Sum(v => v.Minutes));
    }

    public static ClassReportCounts Sum(IEnumerable<ClassReportCounts> parts)
    {
        var list = parts.ToList();
        return new ClassReportCounts(list.Sum(p => p.Held), list.Sum(p => p.Attended), list.Sum(p => p.Absent),
            list.Sum(p => p.Upcoming), list.Sum(p => p.Minutes));
    }
}

/// <summary>Разбивка ученика: предмет и учитель.</summary>
public sealed record ClassReportLine(string Subject, string Teacher, ClassReportCounts Counts);

public sealed record ClassReportRow(ClassReportStudent Student, ClassReportCounts Counts, IReadOnlyList<ClassReportLine> Lines);

/// <summary>
/// Отчёт классного руководителя (этап 7, шаг 3): кто из учеников класса куда ходит и сколько. Чистая логика без базы.
/// В часы — только проведённые занятия, на которых ученик был (как в отчёте учителя); отменённые и
/// «не состоялось» не считаются, неотмеченные — тоже (их видно в отчёте учителя).
/// </summary>
public sealed class ClassReport
{
    private static readonly StringComparer RuOrder = StringComparer.Create(CultureInfo.GetCultureInfo("ru-RU"), false);

    /// <summary>Все ученики класса за период, по алфавиту, — и те, кто никуда не ходит.</summary>
    public required IReadOnlyList<ClassReportRow> Rows { get; init; }
    public required ClassReportCounts Total { get; init; }

    public static ClassReport Build(IEnumerable<ClassReportStudent> students, IEnumerable<ClassReportVisit> visits)
    {
        var byStudent = visits.ToLookup(v => v.StudentId);
        var rows = students
            .OrderBy(s => s.Name, RuOrder)
            .Select(s =>
            {
                var mine = byStudent[s.Id].ToList();
                var lines = mine.GroupBy(v => (v.Subject, v.Teacher))
                    .Select(g => new ClassReportLine(g.Key.Subject, g.Key.Teacher, ClassReportCounts.From(g)))
                    .Where(l => l.Counts.Held + l.Counts.Upcoming > 0)
                    .OrderBy(l => l.Subject, RuOrder).ThenBy(l => l.Teacher, RuOrder)
                    .ToList();
                return new ClassReportRow(s, ClassReportCounts.From(mine), lines);
            })
            .ToList();
        return new ClassReport { Rows = rows, Total = ClassReportCounts.Sum(rows.Select(r => r.Counts)) };
    }
}
