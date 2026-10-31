using Erudit.Domain;
using Erudit.Domain.Calendar;
using Erudit.Domain.Lessons;
using Erudit.Infrastructure.Persistence;
using Microsoft.EntityFrameworkCore;

namespace Erudit.Infrastructure.Lessons;

/// <summary>Занятие, которое закончилось, но не отмечено. Время — по школьным часам.</summary>
public sealed record UnmarkedLesson(int Id, DateTime Start, int DurationMinutes, string Subject);

/// <summary>
/// Загружает неделю учителя для сетки: занятия, звонки, производственный календарь и каникулы.
/// Раскладку по клеткам делает <see cref="TeacherWeek"/> (Domain).
/// </summary>
public sealed class TeacherWeekService(IDbContextFactory<EruditDbContext> dbFactory, TimeProvider time)
{
    /// <summary>Сегодня по школьным часам.</summary>
    public DateOnly Today => DateOnly.FromDateTime(SchoolTime.ToSchool(time.GetUtcNow().UtcDateTime));

    /// <summary>Сейчас по школьным часам: занятие, закончившееся раньше, пора отметить.</summary>
    public DateTime Now => SchoolTime.ToSchool(time.GetUtcNow().UtcDateTime);

    /// <summary>
    /// Неотмеченные занятия учителя: уже закончились, но всё ещё «запланированы», от старых к новым.
    /// Занятия закрытых месяцев не показываем — отметить их всё равно нельзя.
    /// </summary>
    public async Task<IReadOnlyList<UnmarkedLesson>> UnmarkedAsync(int teacherId, CancellationToken ct = default)
    {
        var nowUtc = time.GetUtcNow().UtcDateTime;
        await using var db = await dbFactory.CreateDbContextAsync(ct);
        var closed = (await db.ClosedPeriods.Select(p => p.Month).ToListAsync(ct)).ToHashSet();
        var lessons = await db.Lessons
            .Where(l => l.TeacherId == teacherId && l.Status == LessonStatus.Planned && l.StartsAt < nowUtc)
            .OrderBy(l => l.StartsAt)
            .Select(l => new { l.Id, l.StartsAt, l.DurationMinutes, Subject = l.Subject.Name })
            .ToListAsync(ct);
        return lessons
            .Where(l => l.StartsAt.AddMinutes(l.DurationMinutes) <= nowUtc)
            .Select(l => new UnmarkedLesson(l.Id, SchoolTime.ToSchool(l.StartsAt), l.DurationMinutes, l.Subject))
            .Where(l => !closed.Contains(new DateOnly(l.Start.Year, l.Start.Month, 1)))
            .ToList();
    }

    /// <summary>Понедельник недели, в которую входит дата.</summary>
    public static DateOnly MondayOf(DateOnly date) => date.AddDays(-(((int)date.DayOfWeek + 6) % 7));

    public async Task<TeacherWeek> LoadAsync(int teacherId, DateOnly monday, CancellationToken ct = default)
    {
        var sunday = monday.AddDays(6);
        var fromUtc = SchoolTime.ToUtc(monday, TimeOnly.MinValue);
        var toUtc = SchoolTime.ToUtc(monday.AddDays(7), TimeOnly.MinValue);

        // Контекст — на одну загрузку: страница Blazor живёт долго, а DbContext рассчитан на короткую работу.
        await using var db = await dbFactory.CreateDbContextAsync(ct);

        // Select — чтобы забрать из базы только нужные поля и сразу посчитать записанных.
        var lessons = await db.Lessons
            .Where(l => l.TeacherId == teacherId && l.StartsAt >= fromUtc && l.StartsAt < toUtc)
            .Select(l => new
            {
                l.Id, l.StartsAt, l.DurationMinutes, l.LessonNumber,
                Subject = l.Subject.Name, SubjectShort = l.Subject.ShortName ?? l.Subject.Name, Room = l.Room.Name,
                Enrolled = l.Enrollments.Count(e => e.Status == EnrollmentStatus.Active),
                l.Capacity, l.Status, l.SeriesId, l.Topic,
            })
            .ToListAsync(ct);
        var periods = await db.LessonPeriods.ToListAsync(ct);
        var days = await db.CalendarDays.Where(d => d.Date >= monday && d.Date <= sunday).ToListAsync(ct);
        var vacations = await db.Vacations.Where(v => v.StartDate <= sunday && v.EndDate >= monday).ToListAsync(ct);

        var weekLessons = lessons.Select(l => new WeekLesson(
            l.Id, SchoolTime.ToSchool(l.StartsAt), l.DurationMinutes, l.LessonNumber, l.Subject, l.SubjectShort, l.Room,
            l.Enrolled, l.Capacity, l.Status, l.SeriesId is not null, l.Topic));

        return TeacherWeek.Build(monday, periods, weekLessons, new WorkingDayCalendar(days), vacations, Today);
    }
}
