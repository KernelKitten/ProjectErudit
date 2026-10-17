using Erudit.Domain.People;
using Erudit.Domain.School;

namespace Erudit.Domain.Lessons;

/// <summary>
/// Регулярное занятие: «каждую среду, 7-й урок, с 01.09 по 25.12».
/// Само по себе ничего не значит для отчётов — по нему создаются отдельные занятия (Lesson).
/// Правило простое: один день недели, каждую неделю. Вторник и четверг — две серии.
/// </summary>
public class Series
{
    public int Id { get; set; }

    public int TeacherId { get; set; }
    public Person Teacher { get; set; } = null!;

    public int SubjectId { get; set; }
    public Subject Subject { get; set; } = null!;

    public int RoomId { get; set; }
    public Room Room { get; set; } = null!;

    public DayOfWeek DayOfWeek { get; set; }

    /// <summary>Номер урока, если время взято из расписания звонков; пусто — время задано вручную.</summary>
    public int? LessonNumber { get; set; }

    /// <summary>Время начала по школьным часам.</summary>
    public TimeOnly StartTime { get; set; }

    public int DurationMinutes { get; set; }
    public int Capacity { get; set; } = 1;

    public string? Topic { get; set; }
    public string? Comment { get; set; }

    public DateOnly FromDate { get; set; }
    public DateOnly ToDate { get; set; }

    public List<Lesson> Lessons { get; set; } = [];
}

/// <summary>
/// Ученик записан на всю серию: получает запись на каждое будущее занятие,
/// в том числе на занятия, которые появятся, если серию продлят.
/// </summary>
public class SeriesEnrollment
{
    public int Id { get; set; }

    public int SeriesId { get; set; }
    public Series Series { get; set; } = null!;

    public int StudentId { get; set; }
    public Person Student { get; set; } = null!;

    public DateTime CreatedAt { get; set; }
}
