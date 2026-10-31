using Erudit.Domain;
using Erudit.Domain.Lessons;
using Microsoft.EntityFrameworkCore;

namespace Erudit.Infrastructure.Lessons;

/// <summary>
/// Отметка занятия (этап 5, правила согласованы 2026-10-31):
///  - отмечать можно с момента начала занятия; исправлять и снимать отметку — пока месяц не закрыт;
///  - «проведено»: посещаемость записанных учеников и фактическая длительность, если она отличается от плановой;
///  - «не состоялось»: посещаемость и фактическая длительность сбрасываются;
///  - отменённое занятие отметить нельзя — сначала его надо вернуть.
/// </summary>
public sealed partial class LessonService
{
    /// <summary>Самое долгое занятие, которое можно указать при отметке, в минутах.</summary>
    public const int MaxActualMinutes = 600;

    /// <summary>Отметить «проведено». <paramref name="absentIds"/> — кто отсутствовал; остальные записанные присутствовали.</summary>
    public async Task MarkHeldAsync(int lessonId, IReadOnlyCollection<int> absentIds, int actualMinutes, LessonActor actor,
        CancellationToken ct = default)
    {
        if (actualMinutes is <= 0 or > MaxActualMinutes)
        {
            throw new InvalidOperationException($"Длительность — от 1 до {MaxActualMinutes} минут.");
        }
        var lesson = await LoadForMarkAsync(lessonId, actor, ct);
        lesson.Status = LessonStatus.Held;
        // Храним только отличие от плана: иначе при переносе занятия «факт» остался бы старым.
        lesson.ActualDurationMinutes = actualMinutes == lesson.DurationMinutes ? null : actualMinutes;
        foreach (var e in lesson.Enrollments)
        {
            e.Attendance = e.Status != EnrollmentStatus.Active ? Attendance.Unknown
                : absentIds.Contains(e.StudentId) ? Attendance.Absent : Attendance.Present;
        }
        await db.SaveChangesAsync(ct);
    }

    /// <summary>Отметить «не состоялось».</summary>
    public async Task MarkNotHeldAsync(int lessonId, LessonActor actor, CancellationToken ct = default)
    {
        var lesson = await LoadForMarkAsync(lessonId, actor, ct);
        lesson.Status = LessonStatus.NotHeld;
        ClearMark(lesson);
        await db.SaveChangesAsync(ct);
    }

    /// <summary>Снять отметку: занятие снова «запланировано», его можно изменить или отменить.</summary>
    public async Task UnmarkAsync(int lessonId, LessonActor actor, CancellationToken ct = default)
    {
        var lesson = await LoadForMarkAsync(lessonId, actor, ct);
        lesson.Status = LessonStatus.Planned;
        ClearMark(lesson);
        await db.SaveChangesAsync(ct);
    }

    private static void ClearMark(Lesson lesson)
    {
        lesson.ActualDurationMinutes = null;
        foreach (var e in lesson.Enrollments)
        {
            e.Attendance = Attendance.Unknown;
        }
    }

    private async Task<Lesson> LoadForMarkAsync(int lessonId, LessonActor actor, CancellationToken ct)
    {
        var lesson = await db.Lessons.Include(l => l.Enrollments).SingleOrDefaultAsync(l => l.Id == lessonId, ct)
                     ?? throw new InvalidOperationException("Занятие не найдено.");
        var errors = new List<string>();
        if (!actor.IsManager && lesson.TeacherId != actor.PersonId)
        {
            errors.Add("Учитель может менять только свои занятия.");
        }
        if (lesson.Status == LessonStatus.Cancelled)
        {
            errors.Add("Занятие отменено — сначала верните его.");
        }
        if (lesson.StartsAt > time.GetUtcNow().UtcDateTime)
        {
            errors.Add("Занятие ещё не началось.");
        }
        await CheckClosedAsync([SchoolDate(lesson.StartsAt)], errors, ct);
        if (errors.Count > 0)
        {
            throw new InvalidOperationException(string.Join(" ", errors));
        }
        return lesson;
    }
}
