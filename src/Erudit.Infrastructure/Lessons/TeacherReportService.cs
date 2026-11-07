using System.Globalization;
using Erudit.Domain;
using Erudit.Domain.Lessons;
using Erudit.Infrastructure.Persistence;
using Microsoft.EntityFrameworkCore;

namespace Erudit.Infrastructure.Lessons;

/// <summary>Период для выбора в отчёте: «Октябрь 2026», «1 четверть», «2026/27».</summary>
public sealed record ReportPeriod(string Label, DateOnly From, DateOnly To);

/// <summary>Данные отчёта учителя; подсчёт — <see cref="TeacherReport"/>. Контекст на одну загрузку, как у страниц Blazor.</summary>
public sealed class TeacherReportService(IDbContextFactory<EruditDbContext> dbFactory, TimeProvider time)
{
    private static readonly CultureInfo Ru = CultureInfo.GetCultureInfo("ru-RU");

    /// <param name="from">Первый день периода (по школьным часам), включительно.</param>
    /// <param name="to">Последний день периода, включительно.</param>
    public async Task<TeacherReport> LoadAsync(int teacherId, DateOnly from, DateOnly to, CancellationToken ct = default)
    {
        await using var db = await dbFactory.CreateDbContextAsync(ct);
        var lessons = await LoadLessonsAsync(db, from, to, q => q.Where(l => l.TeacherId == teacherId), ct);
        return TeacherReport.Build(lessons.Select(x => x.Lesson));
    }

    /// <summary>
    /// Сводка завуча (этап 7, шаг 2): по строке на учителя — тот же <see cref="TeacherReport"/>, что и в отчёте учителя,
    /// поэтому цифры совпадают. Работающие учителя — всегда (и без занятий), уволенные — только если у них были занятия.
    /// </summary>
    /// <param name="subjectId">Только занятия этого предмета; null — все.</param>
    public async Task<List<TeacherSummaryRow>> SummaryAsync(DateOnly from, DateOnly to, int? subjectId, CancellationToken ct = default)
    {
        await using var db = await dbFactory.CreateDbContextAsync(ct);
        var lessons = await LoadLessonsAsync(db, from, to,
            q => subjectId is { } id ? q.Where(l => l.SubjectId == id) : q, ct);
        var byTeacher = lessons.ToLookup(x => x.TeacherId, x => x.Lesson);
        var withLessons = byTeacher.Select(g => g.Key).ToList();
        var teachers = await db.People.AsNoTracking()
            .Where(p => withLessons.Contains(p.Id) || (p.IsActive && p.Roles.Any(r => r.Role == Domain.People.Role.Teacher)))
            .OrderBy(p => p.LastName).ThenBy(p => p.FirstName).ThenBy(p => p.MiddleName)
            .Select(p => new { p.Id, p.LastName, p.FirstName, p.MiddleName, p.IsActive })
            .ToListAsync(ct);
        return teachers
            .Select(t => TeacherReport.Build(byTeacher[t.Id])
                .ToSummary(t.Id, Domain.People.Person.Short(t.LastName, t.FirstName, t.MiddleName), t.IsActive))
            .ToList();
    }

    /// <summary>Занятия периода для отчёта: записанные ученики с классом на дату занятия. Один контекст — одна загрузка.</summary>
    private async Task<List<(int TeacherId, ReportLesson Lesson)>> LoadLessonsAsync(EruditDbContext db, DateOnly from, DateOnly to,
        Func<IQueryable<Lesson>, IQueryable<Lesson>> filter, CancellationToken ct)
    {
        var fromUtc = SchoolTime.ToUtc(from, TimeOnly.MinValue);
        var toUtc = SchoolTime.ToUtc(to.AddDays(1), TimeOnly.MinValue);
        var now = time.GetUtcNow().UtcDateTime;

        var lessons = await filter(db.Lessons.AsNoTracking()
                .Include(l => l.Subject).Include(l => l.Room)
                .Include(l => l.Enrollments.Where(e => e.Status == EnrollmentStatus.Active)).ThenInclude(e => e.Student)
                .Where(l => l.StartsAt >= fromUtc && l.StartsAt < toUtc))
            .AsSplitQuery()
            .ToListAsync(ct);

        var ids = lessons.SelectMany(l => l.Enrollments).Select(e => e.StudentId).Distinct().ToList();
        var memberships = (await db.ClassMemberships
                .Where(m => ids.Contains(m.StudentId))
                .Select(m => new { m.StudentId, m.FromDate, m.ToDate, m.SchoolClass.Grade, m.SchoolClass.Letter })
                .ToListAsync(ct))
            .ToLookup(m => m.StudentId);

        return lessons.Select(l =>
        {
            var start = SchoolTime.ToSchool(l.StartsAt);
            var date = DateOnly.FromDateTime(start);
            var students = l.Enrollments.Select(e =>
            {
                var m = memberships[e.StudentId].FirstOrDefault(x => x.FromDate <= date && (x.ToDate == null || x.ToDate >= date));
                return new ReportStudent(e.StudentId, $"{e.Student.LastName} {e.Student.FirstName}",
                    m is null ? null : $"{m.Grade}{m.Letter}", e.Attendance);
            }).ToList();
            return (l.TeacherId, new ReportLesson(l.Id, date, TimeOnly.FromDateTime(start), l.LessonNumber, l.Subject.Name, l.Room.Name,
                l.Status, l.StartsAt.AddMinutes(l.DurationMinutes) <= now, l.ReportedMinutes, students));
        }).ToList();
    }

    /// <summary>Что предложить в списке: месяцы учебного года до текущего (новые сверху), четверти, весь год.</summary>
    public async Task<IReadOnlyList<ReportPeriod>> PeriodsAsync(CancellationToken ct = default)
    {
        var today = DateOnly.FromDateTime(SchoolTime.ToSchool(time.GetUtcNow().UtcDateTime));
        var thisMonth = new DateOnly(today.Year, today.Month, 1);

        await using var db = await dbFactory.CreateDbContextAsync(ct);
        var year = await db.AcademicYears.AsNoTracking().FirstOrDefaultAsync(y => y.StartDate <= today && y.EndDate >= today, ct);

        var result = new List<ReportPeriod>();
        var firstMonth = year is null ? thisMonth : new DateOnly(year.StartDate.Year, year.StartDate.Month, 1);
        for (var month = thisMonth; month >= firstMonth; month = month.AddMonths(-1))
        {
            var label = month.ToString("MMMM yyyy", Ru);
            result.Add(new ReportPeriod(char.ToUpper(label[0], Ru) + label[1..], month, month.AddMonths(1).AddDays(-1)));
        }
        if (year is not null)
        {
            var quarters = await db.Quarters.AsNoTracking().Where(q => q.AcademicYearId == year.Id).OrderBy(q => q.Number).ToListAsync(ct);
            result.AddRange(quarters.Select(q => new ReportPeriod($"{q.Number} четверть", q.StartDate, q.EndDate)));
            result.Add(new ReportPeriod($"Учебный год {year.Name}", year.StartDate, year.EndDate));
        }
        return result;
    }
}
