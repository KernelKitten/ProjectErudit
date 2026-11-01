using Erudit.Domain;
using Erudit.Domain.Lessons;
using Erudit.Domain.Notifications;
using Erudit.Infrastructure.Notifications;
using Microsoft.EntityFrameworkCore;

namespace Erudit.Infrastructure.Lessons;

/// <summary>
/// Сообщения об изменениях занятий (этап 6). Только о будущих занятиях; одно действие — одно сообщение человеку,
/// даже если затронута вся серия. Ставятся в очередь до SaveChanges и сохраняются вместе с изменением.
/// </summary>
public sealed partial class LessonService
{
    /// <summary>Занятия для текста: описание ближайшего и всего набора. Нет будущих — null.</summary>
    private sealed record LessonSet(string Description, DateTime LastStart);

    private LessonNotifications Notifications(LessonActor actor) => new(db, time.GetUtcNow().UtcDateTime, actor.PersonId);

    private async Task<LessonSet?> DescribeFutureAsync(LessonNotifications notifications, IEnumerable<Lesson> lessons, CancellationToken ct)
    {
        var now = time.GetUtcNow().UtcDateTime;
        var future = lessons.Where(l => l.StartsAt > now).OrderBy(l => l.StartsAt).ToList();
        if (future.Count == 0)
        {
            return null;
        }
        var first = await InfoAsync(notifications, future[0], ct);
        return new LessonSet(
            NotificationText.DescribeSeries(first, SchoolDate(future[^1].StartsAt), future.Count),
            future[^1].StartsAt);
    }

    private static Task<LessonInfo> InfoAsync(LessonNotifications notifications, Lesson l, CancellationToken ct) =>
        notifications.InfoAsync(l.TeacherId, l.SubjectId, l.RoomId, l.StartsAt, l.DurationMinutes, l.LessonNumber, ct);

    /// <summary>Создание: ученикам — одно сообщение обо всех сериях (Вт + Чт), учителю — если создал не он сам.</summary>
    private async Task NotifyCreatedAsync(IReadOnlyList<IReadOnlyList<Lesson>> groups, IReadOnlyCollection<int> students, int teacherId,
        LessonActor actor, CancellationToken ct)
    {
        var notifications = Notifications(actor);
        var sets = new List<LessonSet>();
        foreach (var group in groups)
        {
            if (await DescribeFutureAsync(notifications, group, ct) is { } set)
            {
                sets.Add(set);
            }
        }
        if (sets.Count == 0)
        {
            return;
        }
        var description = string.Join(";\n", sets.Select(s => s.Description));
        var expiresAt = sets.Max(s => s.LastStart);
        await notifications.ToStudentsAsync(students, NotificationText.Added(description), expiresAt, ct);
        await NotifyTeacherAsync(notifications, teacherId, NotificationText.TeacherLessonAdded(description), actor, expiresAt, ct);
    }

    /// <summary>
    /// Изменение: кого добавили, кого убрали и кто остался (этим — только если изменилось важное:
    /// предмет, кабинет, дата, время). Учителю — если менял не он сам: с перечнем учеников, если состав изменился.
    /// </summary>
    private async Task NotifyChangedAsync(LessonNotifications notifications, LessonSet? before, LessonSet? after, bool important,
        IReadOnlyCollection<int> oldStudents, IReadOnlyCollection<int> newStudents, int teacherId, LessonActor actor, CancellationToken ct)
    {
        if (before is null && after is null)
        {
            return;
        }
        var expiresAt = new[] { before?.LastStart, after?.LastStart }.Max()!.Value;
        var added = newStudents.Except(oldStudents).ToList();
        var removed = oldStudents.Except(newStudents).ToList();
        var stayed = oldStudents.Intersect(newStudents).ToList();

        if (after is not null)
        {
            await notifications.ToStudentsAsync(added, NotificationText.Added(after.Description), expiresAt, ct);
        }
        if (before is not null)
        {
            await notifications.ToStudentsAsync(removed, NotificationText.Removed(before.Description), expiresAt, ct);
        }
        var stayedText = important && before is not null && after is not null
            ? NotificationText.Changed(before.Description, after.Description)
            : null;
        if (stayedText is not null)
        {
            await notifications.ToStudentsAsync(stayed, stayedText, expiresAt, ct);
        }

        var delta = await StudentDeltaAsync(added, removed, ct);
        var teacherText = stayedText ?? (delta.Length > 0 ? $"✏️ Изменение занятия: {(after ?? before)!.Description}." : null);
        if (teacherText is not null)
        {
            await NotifyTeacherAsync(notifications, teacherId, teacherText + delta, actor, expiresAt, ct);
        }
    }

    private async Task NotifyCancelledAsync(IReadOnlyList<Lesson> lessons, string? reason, LessonActor actor, CancellationToken ct)
    {
        var notifications = Notifications(actor);
        if (await DescribeFutureAsync(notifications, lessons, ct) is not { } set)
        {
            return;
        }
        var now = time.GetUtcNow().UtcDateTime;
        var ids = lessons.Where(l => l.StartsAt > now).Select(l => l.Id).ToList();
        var students = await db.Enrollments
            .Where(e => ids.Contains(e.LessonId) && e.Status == EnrollmentStatus.Active)
            .Select(e => e.StudentId).Distinct().ToListAsync(ct);
        var text = NotificationText.Cancelled(set.Description, reason);
        await notifications.ToStudentsAsync(students, text, set.LastStart, ct);
        await NotifyTeacherAsync(notifications, lessons[0].TeacherId, text, actor, set.LastStart, ct);
    }

    private async Task NotifyRestoredAsync(Lesson lesson, LessonActor actor, CancellationToken ct)
    {
        var notifications = Notifications(actor);
        if (await DescribeFutureAsync(notifications, [lesson], ct) is not { } set)
        {
            return;
        }
        var students = await db.Enrollments
            .Where(e => e.LessonId == lesson.Id && e.Status == EnrollmentStatus.Active)
            .Select(e => e.StudentId).ToListAsync(ct);
        var text = NotificationText.Restored(set.Description);
        await notifications.ToStudentsAsync(students, text, set.LastStart, ct);
        await NotifyTeacherAsync(notifications, lesson.TeacherId, text, actor, set.LastStart, ct);
    }

    /// <summary>Занятие изменил завуч или администратор — учитель должен знать, кто это сделал.</summary>
    private async Task NotifyTeacherAsync(LessonNotifications notifications, int teacherId, string text, LessonActor actor, DateTime expiresAt,
        CancellationToken ct)
    {
        if (teacherId == actor.PersonId)
        {
            return;
        }
        var author = await db.People.AsNoTracking().SingleOrDefaultAsync(p => p.Id == actor.PersonId, ct);
        await notifications.ToPersonAsync(teacherId, author is null ? text : $"{text}\nИзменения внёс(ла): {author.ShortName}.", expiresAt, ct);
    }

    private async Task<string> StudentDeltaAsync(List<int> added, List<int> removed, CancellationToken ct)
    {
        async Task<string> NamesAsync(string title, List<int> ids)
        {
            if (ids.Count == 0)
            {
                return "";
            }
            var people = await db.People.AsNoTracking().Where(p => ids.Contains(p.Id)).ToListAsync(ct);
            return $"\n{title}: {string.Join(", ", people.Select(p => p.ShortName).Order())}.";
        }
        return await NamesAsync("Записаны", added) + await NamesAsync("Убраны", removed);
    }

    /// <summary>Кто записан на будущие занятия из набора (на любое из них).</summary>
    private HashSet<int> ActiveOnFuture(IEnumerable<Lesson> lessons)
    {
        var now = time.GetUtcNow().UtcDateTime;
        return lessons.Where(l => l.StartsAt > now)
            .SelectMany(l => l.Enrollments)
            .Where(e => e.Status == EnrollmentStatus.Active)
            .Select(e => e.StudentId)
            .ToHashSet();
    }
}
