using Erudit.Domain;
using Erudit.Domain.Notifications;
using Erudit.Infrastructure.Persistence;
using Microsoft.EntityFrameworkCore;

namespace Erudit.Infrastructure.Notifications;

/// <summary>
/// Ставит сообщения о занятиях в очередь. Только добавляет строки в контекст: их сохранит тот же
/// SaveChanges, что и само изменение, — одной транзакцией. Автор изменения сообщения о нём не получает.
/// </summary>
internal sealed class LessonNotifications(EruditDbContext db, DateTime now, int actorId)
{
    /// <summary>Ученикам (у кого есть MAX) и родителям с «Все уведомления о ребёнке».</summary>
    public async Task ToStudentsAsync(IReadOnlyCollection<int> studentIds, string text, DateTime expiresAt, CancellationToken ct)
    {
        if (studentIds.Count == 0)
        {
            return;
        }
        var students = await db.People
            .Where(p => studentIds.Contains(p.Id) && p.IsActive && p.MaxUserId != null)
            .Select(p => p.Id)
            .ToListAsync(ct);
        foreach (var id in students)
        {
            Add(id, text, expiresAt);
        }

        var parents = await db.ParentLinks
            .Where(l => studentIds.Contains(l.StudentId) && l.NotifyAll && l.Parent.IsActive && l.Parent.MaxUserId != null)
            .Select(l => new { l.ParentId, Child = l.Student.LastName + " " + l.Student.FirstName })
            .ToListAsync(ct);
        // Двое детей на одном занятии — одно сообщение родителю, а не два.
        foreach (var parent in parents.GroupBy(p => p.ParentId))
        {
            Add(parent.Key, NotificationText.ForParent(parent.Select(p => p.Child).Order(), text), expiresAt);
        }
    }

    public async Task ToPersonAsync(int personId, string text, DateTime expiresAt, CancellationToken ct)
    {
        if (await db.People.AnyAsync(p => p.Id == personId && p.IsActive && p.MaxUserId != null, ct))
        {
            Add(personId, text, expiresAt);
        }
    }

    /// <summary>Описание занятия для текста: названия берём из базы по id.</summary>
    public async Task<LessonInfo> InfoAsync(int teacherId, int subjectId, int roomId, DateTime startsAt, int durationMinutes,
        int? lessonNumber, CancellationToken ct)
    {
        var subject = await db.Subjects.Where(s => s.Id == subjectId).Select(s => s.Name).SingleAsync(ct);
        var room = await db.Rooms.Where(r => r.Id == roomId).Select(r => r.Name).SingleAsync(ct);
        var teacher = await db.People.AsNoTracking().SingleAsync(p => p.Id == teacherId, ct);
        var start = SchoolTime.ToSchool(startsAt);
        return new LessonInfo(subject, DateOnly.FromDateTime(start), TimeOnly.FromDateTime(start), durationMinutes, lessonNumber, room, teacher.ShortName);
    }

    private void Add(int recipientId, string text, DateTime expiresAt)
    {
        if (recipientId == actorId)
        {
            return;
        }
        db.Notifications.Add(new Notification
        {
            RecipientId = recipientId,
            Text = text,
            CreatedAt = now,
            NextAttemptAt = now,
            ExpiresAt = expiresAt,
        });
    }
}
