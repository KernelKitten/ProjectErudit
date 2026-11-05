using Erudit.Domain;
using Erudit.Domain.Lessons;
using Erudit.Domain.Notifications;
using Erudit.Domain.People;
using Erudit.Infrastructure.Lessons;
using Erudit.Infrastructure.Persistence;
using Erudit.Infrastructure.School;
using Hangfire;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;

namespace Erudit.Infrastructure.Notifications;

/// <summary>
/// Всё, что отправляется по времени (этап 6, шаг 3, решено 2026-11-04). Hangfire запускает задачу каждую минуту,
/// она смотрит на занятия как они есть сейчас и ставит в очередь то, чему пора:
///  - ученику и родителю с «Все уведомления» — накануне в 17:00 и в день занятия в 7:00, все занятия дня одним сообщением;
///  - учителю — за 2 часа (если это не раньше 7:00) и за 15 минут, с перечнем записанных;
///  - учителю сразу после занятия — кнопки «Проведено» / «Не состоялось»; ученику в напоминаниях — «Не смогу прийти»;
///  - учителю в 19:00 — сколько занятий не отмечено, со ссылкой на сайт;
///  - родителю в воскресенье в 18:00 — сводка: прошлая неделя и занятия на следующую.
/// Момент пропущен меньше чем на 30 минут (сайт перезапускался) — сообщение ещё уходит; позже — уже нет.
/// Пропущенное из-за поздней записи не досылается. Дубли отсекает уникальный <see cref="Notification.Key"/>.
/// </summary>
public sealed class NotificationScheduleJob(
    EruditDbContext db,
    TimeProvider time,
    IOptions<SiteOptions> site,
    ILogger<NotificationScheduleJob> logger)
{
    public const string RecurringJobId = "notifications-schedule";

    public static readonly TimeOnly StudentEvening = new(17, 0);
    public static readonly TimeOnly StudentMorning = new(7, 0);
    public static readonly TimeOnly TeacherEarliest = new(7, 0);
    public static readonly TimeOnly UnmarkedAt = new(19, 0);
    public static readonly TimeOnly WeeklyAt = new(18, 0);
    public static readonly int[] TeacherMinutesBefore = [120, 15];

    /// <summary>Насколько можно опоздать с сообщением по времени.</summary>
    public static readonly TimeSpan Grace = TimeSpan.FromMinutes(30);

    /// <summary>Прошлый запуск ещё идёт — новый не начинаем; не вышло — не повторяем: через минуту будет следующий запуск.</summary>
    [DisableConcurrentExecution(timeoutInSeconds: 60)]
    [AutomaticRetry(Attempts = 0)]
    public Task RunAsync(CancellationToken ct) => RunAtAsync(time.GetUtcNow().UtcDateTime, ct);

    /// <summary>Отдельный метод со временем — чтобы тест мог проверить любой момент. Возвращает, сколько поставлено в очередь.</summary>
    public async Task<int> RunAtAsync(DateTime nowUtc, CancellationToken ct = default)
    {
        var today = DateOnly.FromDateTime(SchoolTime.ToSchool(nowUtc));
        var due = new List<Notification>();

        if (IsDue(today, StudentEvening, nowUtc))
        {
            due.AddRange(await StudentDayAsync(today.AddDays(1), tomorrow: true, nowUtc, ct));
        }
        if (IsDue(today, StudentMorning, nowUtc))
        {
            due.AddRange(await StudentDayAsync(today, tomorrow: false, nowUtc, ct));
        }
        foreach (var minutes in TeacherMinutesBefore)
        {
            due.AddRange(await TeacherSoonAsync(minutes, nowUtc, ct));
        }
        due.AddRange(await EndedAsync(nowUtc, ct));
        if (IsDue(today, UnmarkedAt, nowUtc))
        {
            due.AddRange(await UnmarkedAsync(today, nowUtc, ct));
        }
        if (today.DayOfWeek == DayOfWeek.Sunday && IsDue(today, WeeklyAt, nowUtc))
        {
            due.AddRange(await WeeklyAsync(today, nowUtc, ct));
        }
        if (due.Count == 0)
        {
            return 0;
        }

        var keys = due.Select(n => n.Key).ToList();
        var sent = (await db.Notifications.Where(n => keys.Contains(n.Key)).Select(n => n.Key).ToListAsync(ct)).ToHashSet();
        var fresh = due.Where(n => !sent.Contains(n.Key)).ToList();
        db.Notifications.AddRange(fresh);
        await db.SaveChangesAsync(ct);
        if (fresh.Count > 0)
        {
            logger.LogInformation("Сообщения по времени: в очереди {Count}", fresh.Count);
        }
        return fresh.Count;
    }

    private static bool IsDue(DateOnly day, TimeOnly at, DateTime nowUtc)
    {
        var moment = SchoolTime.ToUtc(day, at);
        return moment <= nowUtc && nowUtc < moment + Grace;
    }

    private static Notification New(int recipientId, string key, string text, DateTime nowUtc, DateTime expiresAt) => new()
    {
        RecipientId = recipientId, Key = key, Text = text, CreatedAt = nowUtc, NextAttemptAt = nowUtc, ExpiresAt = expiresAt,
    };

    // ---------------------------------------------------------------- Ученики

    private async Task<List<Notification>> StudentDayAsync(DateOnly date, bool tomorrow, DateTime nowUtc, CancellationToken ct)
    {
        var (from, to) = (SchoolTime.ToUtc(date, TimeOnly.MinValue), SchoolTime.ToUtc(date.AddDays(1), TimeOnly.MinValue));
        var rows = await db.Enrollments
            .Where(e => e.Status == EnrollmentStatus.Active && e.Student.IsActive
                        && e.Lesson.Status == LessonStatus.Planned && e.Lesson.StartsAt >= from && e.Lesson.StartsAt < to
                        && e.Lesson.StartsAt > nowUtc)
            .Select(e => new
            {
                e.StudentId,
                e.LessonId,
                StudentMax = e.Student.MaxUserId,
                Child = e.Student.LastName + " " + e.Student.FirstName,
                ChildFirst = e.Student.FirstName,
                Lesson = new LessonRow(e.Lesson.StartsAt, e.Lesson.DurationMinutes, e.Lesson.LessonNumber, e.Lesson.Subject.Name, e.Lesson.Room.Name,
                    e.Lesson.Teacher.LastName, e.Lesson.Teacher.FirstName, e.Lesson.Teacher.MiddleName),
            })
            .ToListAsync(ct);
        if (rows.Count == 0)
        {
            return [];
        }
        var studentIds = rows.Select(r => r.StudentId).Distinct().ToList();
        var parents = await db.ParentLinks
            .Where(l => studentIds.Contains(l.StudentId) && l.NotifyAll && l.Parent.IsActive && l.Parent.MaxUserId != null)
            .Select(l => new { l.ParentId, l.StudentId })
            .ToListAsync(ct);

        // Кому и какие строки: самому ученику — без имени, родителю — с именем ребёнка. У каждой строки — кнопка отказа.
        var lines = rows.Where(r => r.StudentMax != null)
            .Select(r => (Recipient: r.StudentId, r.Lesson, Line: NotificationText.LessonLine(r.Lesson.Info),
                Button: LessonButtons.SkipButton(r.LessonId, r.StudentId, null, r.Lesson.StartsAt, r.Lesson.Subject)))
            .Concat(parents.SelectMany(p => rows.Where(r => r.StudentId == p.StudentId)
                .Select(r => (Recipient: p.ParentId, r.Lesson, Line: $"{r.Child}: {NotificationText.LessonLine(r.Lesson.Info)}",
                    Button: LessonButtons.SkipButton(r.LessonId, r.StudentId, r.ChildFirst, r.Lesson.StartsAt, r.Lesson.Subject)))));
        var kind = tomorrow ? "evening" : "morning";
        return lines
            .GroupBy(x => x.Recipient)
            .Select(g =>
            {
                var ordered = g.OrderBy(x => x.Lesson.StartsAt).ThenBy(x => x.Line).ToList();
                var notification = New(g.Key, $"{kind}:{g.Key}:{date:yyyyMMdd}",
                    NotificationText.StudentDay(date, tomorrow, ordered.Select(x => x.Line)), nowUtc, ordered[^1].Lesson.StartsAt);
                notification.Buttons = NotificationButton.ToJson(ordered.Select(x => x.Button).ToList());
                return notification;
            })
            .ToList();
    }

    // ---------------------------------------------------------------- Учителя

    /// <summary>
    /// Занятия, у которых момент «за N минут» уже наступил, но прошло меньше <see cref="Grace"/>.
    /// Начавшиеся не берём: опоздание в 30 минут больше, чем «за 15 минут».
    /// </summary>
    private async Task<List<Notification>> TeacherSoonAsync(int minutesBefore, DateTime nowUtc, CancellationToken ct)
    {
        var to = nowUtc.AddMinutes(minutesBefore);
        var from = to - Grace < nowUtc ? nowUtc : to - Grace;
        var lessons = await db.Lessons
            .Where(l => l.Status == LessonStatus.Planned && l.StartsAt > from && l.StartsAt <= to
                        && l.Teacher.IsActive && l.Teacher.MaxUserId != null)
            .Select(l => new
            {
                l.Id,
                l.TeacherId,
                l.Capacity,
                Lesson = new LessonRow(l.StartsAt, l.DurationMinutes, l.LessonNumber, l.Subject.Name, l.Room.Name,
                    l.Teacher.LastName, l.Teacher.FirstName, l.Teacher.MiddleName),
                Students = l.Enrollments.Where(e => e.Status == EnrollmentStatus.Active)
                    .Select(e => new { e.Student.LastName, e.Student.FirstName, e.Student.MiddleName }).ToList(),
            })
            .ToListAsync(ct);
        return lessons
            // «За 2 часа» к занятию в 8:00 пришло бы в 6:00 — тогда учителю хватит напоминания за 15 минут.
            .Where(l => TimeOnly.FromDateTime(SchoolTime.ToSchool(l.Lesson.StartsAt.AddMinutes(-minutesBefore))) >= TeacherEarliest
                        || minutesBefore < 60)
            .Select(l => New(l.TeacherId, $"t{minutesBefore}:{l.Id}:{l.Lesson.StartsAt:yyyyMMddHHmm}",
                NotificationText.TeacherSoon(minutesBefore, l.Lesson.Info,
                    l.Students.Select(s => Person.Short(s.LastName, s.FirstName, s.MiddleName)).Order().ToList(), l.Capacity),
                nowUtc, l.Lesson.StartsAt))
            .ToList();
    }

    /// <summary>Сразу после конца занятия (опоздание — до <see cref="Grace"/>) — учителю с кнопками отметки.</summary>
    private async Task<List<Notification>> EndedAsync(DateTime nowUtc, CancellationToken ct)
    {
        // Конец = начало + длительность; длительность до 10 часов — берём начавшиеся с запасом и проверяем конец здесь.
        var from = nowUtc - Grace - TimeSpan.FromMinutes(LessonService.MaxActualMinutes);
        var lessons = await db.Lessons
            .Where(l => l.Status == LessonStatus.Planned && l.StartsAt > from && l.StartsAt < nowUtc
                        && l.Teacher.IsActive && l.Teacher.MaxUserId != null)
            .Select(l => new
            {
                l.Id,
                l.TeacherId,
                Lesson = new LessonRow(l.StartsAt, l.DurationMinutes, l.LessonNumber, l.Subject.Name, l.Room.Name,
                    l.Teacher.LastName, l.Teacher.FirstName, l.Teacher.MiddleName),
                Students = l.Enrollments.Where(e => e.Status == EnrollmentStatus.Active)
                    .Select(e => new { e.Student.LastName, e.Student.FirstName, e.Student.MiddleName }).ToList(),
            })
            .ToListAsync(ct);
        return lessons
            .Where(l =>
            {
                var end = l.Lesson.StartsAt.AddMinutes(l.Lesson.DurationMinutes);
                return end <= nowUtc && nowUtc < end + Grace;
            })
            .Select(l =>
            {
                var notification = New(l.TeacherId, $"end:{l.Id}:{l.Lesson.StartsAt:yyyyMMddHHmm}",
                    NotificationText.LessonEnded(l.Lesson.Info,
                        l.Students.Select(s => Person.Short(s.LastName, s.FirstName, s.MiddleName)).Order().ToList()),
                    nowUtc, nowUtc.AddHours(12));
                notification.Buttons = NotificationButton.ToJson(LessonButtons.MarkButtons(l.Id));
                return notification;
            })
            .ToList();
    }

    private async Task<List<Notification>> UnmarkedAsync(DateOnly today, DateTime nowUtc, CancellationToken ct)
    {
        var closed = (await db.ClosedPeriods.Select(p => p.Month).ToListAsync(ct)).ToHashSet();
        var lessons = await db.Lessons
            .Where(l => l.Status == LessonStatus.Planned && l.StartsAt < nowUtc && l.Teacher.IsActive && l.Teacher.MaxUserId != null)
            .Select(l => new { l.TeacherId, l.StartsAt, l.DurationMinutes })
            .ToListAsync(ct);
        var link = string.IsNullOrWhiteSpace(site.Value.BaseUrl) ? null : $"{site.Value.BaseUrl.TrimEnd('/')}/teacher";
        return lessons
            .Where(l => l.StartsAt.AddMinutes(l.DurationMinutes) <= nowUtc)
            .Where(l =>
            {
                var date = SchoolTime.ToSchool(l.StartsAt);
                return !closed.Contains(new DateOnly(date.Year, date.Month, 1)); // в закрытом месяце отметить всё равно нельзя
            })
            .GroupBy(l => l.TeacherId)
            .Select(g => New(g.Key, $"unmarked:{g.Key}:{today:yyyyMMdd}", NotificationText.Unmarked(g.Count(), link), nowUtc, nowUtc.AddHours(3)))
            .ToList();
    }

    // ---------------------------------------------------------------- Родители

    private async Task<List<Notification>> WeeklyAsync(DateOnly sunday, DateTime nowUtc, CancellationToken ct)
    {
        var lastFrom = SchoolTime.ToUtc(sunday.AddDays(-6), TimeOnly.MinValue);
        var nextFrom = SchoolTime.ToUtc(sunday.AddDays(1), TimeOnly.MinValue);
        var nextTo = SchoolTime.ToUtc(sunday.AddDays(8), TimeOnly.MinValue);

        var links = await db.ParentLinks
            .Where(l => l.Parent.IsActive && l.Parent.MaxUserId != null && l.Student.IsActive)
            .Select(l => new { l.ParentId, l.StudentId, Child = l.Student.LastName + " " + l.Student.FirstName })
            .ToListAsync(ct);
        if (links.Count == 0)
        {
            return [];
        }
        var studentIds = links.Select(l => l.StudentId).Distinct().ToList();
        var past = await db.Enrollments
            .Where(e => studentIds.Contains(e.StudentId) && e.Status == EnrollmentStatus.Active && e.Lesson.Status == LessonStatus.Held
                        && e.Lesson.StartsAt >= lastFrom && e.Lesson.StartsAt < nextFrom)
            .Select(e => new { e.StudentId, e.Attendance })
            .ToListAsync(ct);
        var next = await db.Enrollments
            .Where(e => studentIds.Contains(e.StudentId) && e.Status == EnrollmentStatus.Active && e.Lesson.Status == LessonStatus.Planned
                        && e.Lesson.StartsAt >= nextFrom && e.Lesson.StartsAt < nextTo)
            .Select(e => new
            {
                e.StudentId,
                Lesson = new LessonRow(e.Lesson.StartsAt, e.Lesson.DurationMinutes, e.Lesson.LessonNumber, e.Lesson.Subject.Name, e.Lesson.Room.Name,
                    e.Lesson.Teacher.LastName, e.Lesson.Teacher.FirstName, e.Lesson.Teacher.MiddleName),
            })
            .ToListAsync(ct);

        var result = new List<Notification>();
        foreach (var parent in links.GroupBy(l => l.ParentId))
        {
            var children = parent
                .OrderBy(c => c.Child)
                .Select(c => new ChildWeek(c.Child,
                    past.Count(p => p.StudentId == c.StudentId && p.Attendance == Attendance.Present),
                    past.Count(p => p.StudentId == c.StudentId && p.Attendance == Attendance.Absent),
                    next.Where(n => n.StudentId == c.StudentId).OrderBy(n => n.Lesson.StartsAt).Select(n => n.Lesson.Info).ToList()))
                // Ребёнок, у которого ни занятий, ни посещений, — в сводке лишний; никого не осталось — сводку не шлём.
                .Where(c => c.Attended + c.Missed + c.Next.Count > 0)
                .ToList();
            if (children.Count > 0)
            {
                result.Add(New(parent.Key, $"weekly:{parent.Key}:{sunday:yyyyMMdd}", NotificationText.WeeklySummary(children),
                    nowUtc, nowUtc.AddHours(12)));
            }
        }
        return result;
    }
}
