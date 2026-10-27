using Erudit.Domain;
using Erudit.Domain.Calendar;
using Erudit.Domain.Lessons;
using Erudit.Infrastructure.Persistence;
using Microsoft.EntityFrameworkCore;

namespace Erudit.Infrastructure.Lessons;

/// <summary>
/// Загружает неделю учителя для сетки: занятия, звонки, производственный календарь и каникулы.
/// Раскладку по клеткам делает <see cref="TeacherWeek"/> (Domain).
/// </summary>
public sealed class TeacherWeekService(IDbContextFactory<EruditDbContext> dbFactory, TimeProvider time)
{
    /// <summary>Сегодня по школьным часам.</summary>
    public DateOnly Today => DateOnly.FromDateTime(SchoolTime.ToSchool(time.GetUtcNow().UtcDateTime));

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
