using Erudit.Domain;
using Erudit.Domain.Notifications;
using Erudit.Domain.People;

namespace Erudit.Infrastructure.Notifications;

/// <summary>Занятие, как его выбираем из базы одним запросом для текста сообщения; в текст — через <see cref="Info"/>.</summary>
internal sealed record LessonRow(DateTime StartsAt, int DurationMinutes, int? LessonNumber, string Subject, string Room,
    string TeacherLast, string TeacherFirst, string? TeacherMiddle)
{
    public LessonInfo Info
    {
        get
        {
            var start = SchoolTime.ToSchool(StartsAt);
            return new LessonInfo(Subject, DateOnly.FromDateTime(start), TimeOnly.FromDateTime(start), DurationMinutes, LessonNumber, Room,
                Person.Short(TeacherLast, TeacherFirst, TeacherMiddle));
        }
    }
}
