using Erudit.Domain.People;
using Erudit.Domain.School;

namespace Erudit.Domain.Lessons;

public enum LessonStatus
{
    Planned = 1,   // запланировано
    Held = 2,      // проведено (отметил учитель)
    NotHeld = 3,   // не состоялось
    Cancelled = 4, // отменено заранее
}

/// <summary>
/// Одно конкретное занятие. С ним работают отмена, перенос, запись учеников, отметка «проведено» и отчёты.
/// </summary>
public class Lesson
{
    public int Id { get; set; }

    /// <summary>Серия, из которой создано занятие. Пусто — разовое занятие.</summary>
    public int? SeriesId { get; set; }
    public Series? Series { get; set; }

    public int TeacherId { get; set; }
    public Person Teacher { get; set; } = null!;

    public int SubjectId { get; set; }
    public Subject Subject { get; set; } = null!;

    public int RoomId { get; set; }
    public Room Room { get; set; } = null!;

    /// <summary>Начало в UTC.</summary>
    public DateTime StartsAt { get; set; }

    /// <summary>Длительность по плану, в минутах.</summary>
    public int DurationMinutes { get; set; }

    /// <summary>Фактическая длительность, если учитель исправил её при отметке «проведено».</summary>
    public int? ActualDurationMinutes { get; set; }

    /// <summary>Номер урока для показа («7 урок»). Пусто — время задано вручную.</summary>
    public int? LessonNumber { get; set; }

    public int Capacity { get; set; } = 1;
    public string? Topic { get; set; }
    public string? Comment { get; set; }

    public LessonStatus Status { get; set; } = LessonStatus.Planned;

    /// <summary>Причина отмены («болезнь учителя») — для учеников; необязательна. Только у отменённых.</summary>
    public string? CancelReason { get; set; }

    /// <summary>Ученики могут записываться сами (иначе записывает только учитель).</summary>
    public bool SelfSignup { get; set; } = true;

    /// <summary>Для каких параллелей открыта запись: с какой. Пусто — без ограничения снизу.</summary>
    public int? SignupGradeFrom { get; set; }

    /// <summary>По какую параллель открыта запись. Пусто — без ограничения сверху.</summary>
    public int? SignupGradeTo { get; set; }

    public List<Enrollment> Enrollments { get; set; } = [];

    /// <summary>Открыта ли самозапись для ученика этой параллели. Ученик без класса подходит только без ограничений.</summary>
    public bool IsOpenForGrade(int? grade)
    {
        if (!SelfSignup)
        {
            return false;
        }
        if (SignupGradeFrom is null && SignupGradeTo is null)
        {
            return true;
        }
        return grade is { } g && (SignupGradeFrom is null || g >= SignupGradeFrom) && (SignupGradeTo is null || g <= SignupGradeTo);
    }

    /// <summary>Сколько минут идёт в отчёт: фактическая длительность, если указана, иначе плановая.</summary>
    public int ReportedMinutes => ActualDurationMinutes ?? DurationMinutes;
}
