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
        var fromUtc = SchoolTime.ToUtc(from, TimeOnly.MinValue);
        var toUtc = SchoolTime.ToUtc(to.AddDays(1), TimeOnly.MinValue);
        var now = time.GetUtcNow().UtcDateTime;

        await using var db = await dbFactory.CreateDbContextAsync(ct);
        var lessons = await db.Lessons.AsNoTracking()
            .Include(l => l.Subject).Include(l => l.Room)
            .Include(l => l.Enrollments.Where(e => e.Status == EnrollmentStatus.Active)).ThenInclude(e => e.Student)
            .Where(l => l.TeacherId == teacherId && l.StartsAt >= fromUtc && l.StartsAt < toUtc)
            .ToListAsync(ct);

        var ids = lessons.SelectMany(l => l.Enrollments).Select(e => e.StudentId).Distinct().ToList();
        var memberships = (await db.ClassMemberships
                .Where(m => ids.Contains(m.StudentId))
                .Select(m => new { m.StudentId, m.FromDate, m.ToDate, m.SchoolClass.Grade, m.SchoolClass.Letter })
                .ToListAsync(ct))
            .ToLookup(m => m.StudentId);

        return TeacherReport.Build(lessons.Select(l =>
        {
            var start = SchoolTime.ToSchool(l.StartsAt);
            var date = DateOnly.FromDateTime(start);
            var students = l.Enrollments.Select(e =>
            {
                var m = memberships[e.StudentId].FirstOrDefault(x => x.FromDate <= date && (x.ToDate == null || x.ToDate >= date));
                return new ReportStudent(e.StudentId, $"{e.Student.LastName} {e.Student.FirstName}",
                    m is null ? null : $"{m.Grade}{m.Letter}", e.Attendance);
            }).ToList();
            return new ReportLesson(l.Id, date, TimeOnly.FromDateTime(start), l.LessonNumber, l.Subject.Name, l.Room.Name, l.Status,
                l.StartsAt.AddMinutes(l.DurationMinutes) <= now, l.ReportedMinutes, students);
        }));
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
