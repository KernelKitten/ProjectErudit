using System.Globalization;
using Erudit.Domain;
using Erudit.Domain.Accounting;
using Erudit.Domain.Lessons;
using Erudit.Domain.People;
using Erudit.Domain.School;
using Erudit.Infrastructure.Persistence;
using Microsoft.EntityFrameworkCore;

namespace Erudit.Infrastructure.Accounting;

/// <summary>Месяц учебного года для страницы «Закрытие месяцев».</summary>
/// <param name="Month">Первое число месяца.</param>
/// <param name="Ended">Месяц закончился — его можно закрыть.</param>
/// <param name="Unmarked">Закончились, но всё ещё «Запланировано».</param>
/// <param name="Upcoming">Ещё не закончились (только у текущего и будущих месяцев).</param>
public sealed record PeriodMonth(DateOnly Month, bool Ended, int Held, int NotHeld, int Cancelled, int Unmarked, int Upcoming,
    PeriodClosed? Closed);

public sealed record PeriodClosed(string By, DateTime AtUtc);

public sealed record UnmarkedLesson(int Id, DateTime StartsAt, string Teacher, string Subject);

/// <summary>
/// Закрытие и открытие месяца (этап 7, шаг 1, решено 2026-11-06). Закрывают и открывают завуч и администратор.
/// Закрыть можно только закончившийся месяц; неотмеченные занятия не мешают (страница предупреждает и ждёт
/// второго нажатия), после закрытия они так и останутся «Запланировано» и в часы не войдут.
/// Закрытие — строка <see cref="ClosedPeriod"/>, открытие — её удаление; оба попадают в журнал изменений.
/// </summary>
public sealed class PeriodService(EruditDbContext db, TimeProvider time)
{
    private static readonly CultureInfo Ru = CultureInfo.GetCultureInfo("ru-RU");

    private DateTime Now => time.GetUtcNow().UtcDateTime;

    /// <summary>«октябрь 2026».</summary>
    public static string MonthName(DateOnly month) => month.ToString("MMMM yyyy", Ru);

    /// <summary>Начало месяца и начало следующего — в UTC по школьным часам.</summary>
    private static (DateTime From, DateTime To) Bounds(DateOnly month) =>
        (SchoolTime.ToUtc(month, TimeOnly.MinValue), SchoolTime.ToUtc(month.AddMonths(1), TimeOnly.MinValue));

    public async Task<List<PeriodMonth>> MonthsAsync(AcademicYear year, CancellationToken ct = default)
    {
        var first = new DateOnly(year.StartDate.Year, year.StartDate.Month, 1);
        var last = new DateOnly(year.EndDate.Year, year.EndDate.Month, 1);
        var (from, _) = Bounds(first);
        var (_, to) = Bounds(last);
        var now = Now;

        var lessons = await db.Lessons
            .Where(l => l.StartsAt >= from && l.StartsAt < to)
            .Select(l => new { l.StartsAt, l.DurationMinutes, l.Status })
            .ToListAsync(ct);
        var closed = await db.ClosedPeriods
            .Where(p => p.Month >= first && p.Month <= last)
            .Select(p => new { p.Month, p.ClosedAt, p.ClosedBy.LastName, p.ClosedBy.FirstName, p.ClosedBy.MiddleName })
            .ToListAsync(ct);

        var result = new List<PeriodMonth>();
        for (var month = first; month <= last; month = month.AddMonths(1))
        {
            var (start, end) = Bounds(month);
            var inMonth = lessons.Where(l => l.StartsAt >= start && l.StartsAt < end).ToList();
            var planned = inMonth.Where(l => l.Status == LessonStatus.Planned).ToList();
            var unmarked = planned.Count(l => l.StartsAt.AddMinutes(l.DurationMinutes) <= now);
            var c = closed.FirstOrDefault(p => p.Month == month);
            result.Add(new PeriodMonth(month, end <= now,
                inMonth.Count(l => l.Status == LessonStatus.Held),
                inMonth.Count(l => l.Status == LessonStatus.NotHeld),
                inMonth.Count(l => l.Status == LessonStatus.Cancelled),
                unmarked, planned.Count - unmarked,
                c is null ? null : new PeriodClosed(Person.Short(c.LastName, c.FirstName, c.MiddleName), c.ClosedAt)));
        }
        return result;
    }

    /// <summary>Занятия месяца, которые закончились, но не отмечены, — их показываем перед закрытием.</summary>
    public async Task<List<UnmarkedLesson>> UnmarkedAsync(DateOnly month, CancellationToken ct = default)
    {
        var (from, to) = Bounds(month);
        var now = Now;
        var lessons = await db.Lessons
            .Where(l => l.Status == LessonStatus.Planned && l.StartsAt >= from && l.StartsAt < to && l.StartsAt < now)
            .OrderBy(l => l.StartsAt).ThenBy(l => l.Teacher.LastName)
            .Select(l => new
            {
                l.Id, l.StartsAt, l.DurationMinutes, Subject = l.Subject.Name,
                l.Teacher.LastName, l.Teacher.FirstName, l.Teacher.MiddleName,
            })
            .ToListAsync(ct);
        return lessons
            .Where(l => l.StartsAt.AddMinutes(l.DurationMinutes) <= now)
            .Select(l => new UnmarkedLesson(l.Id, l.StartsAt, Person.Short(l.LastName, l.FirstName, l.MiddleName), l.Subject))
            .ToList();
    }

    /// <exception cref="InvalidOperationException">Месяц ещё не закончился или уже закрыт.</exception>
    public async Task CloseAsync(DateOnly month, int personId, CancellationToken ct = default)
    {
        month = new DateOnly(month.Year, month.Month, 1);
        if (Bounds(month).To > Now)
        {
            throw new InvalidOperationException($"Месяц {MonthName(month)} ещё не закончился — закрыть можно только прошедший месяц.");
        }
        if (await db.ClosedPeriods.AnyAsync(p => p.Month == month, ct))
        {
            throw new InvalidOperationException($"Месяц {MonthName(month)} уже закрыт.");
        }
        db.ClosedPeriods.Add(new ClosedPeriod { Month = month, ClosedById = personId, ClosedAt = Now });
        try
        {
            await db.SaveChangesAsync(ct);
        }
        catch (DbUpdateException) // одновременно закрыл кто-то ещё — уникальный индекс по месяцу
        {
            db.ChangeTracker.Clear();
            throw new InvalidOperationException($"Месяц {MonthName(month)} уже закрыт.");
        }
    }

    /// <exception cref="InvalidOperationException">Месяц не закрыт.</exception>
    public async Task OpenAsync(DateOnly month, CancellationToken ct = default)
    {
        month = new DateOnly(month.Year, month.Month, 1);
        var period = await db.ClosedPeriods.SingleOrDefaultAsync(p => p.Month == month, ct)
                     ?? throw new InvalidOperationException($"Месяц {MonthName(month)} не закрыт.");
        db.ClosedPeriods.Remove(period);
        await db.SaveChangesAsync(ct);
    }
}
