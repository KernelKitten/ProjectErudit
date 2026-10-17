using Erudit.Domain.People;

namespace Erudit.Domain.Lessons;

public enum EnrollmentStatus
{
    Active = 1,   // записан
    Declined = 2, // ученик (или родитель) отказался
    Removed = 3,  // учитель убрал из занятия
}

public enum EnrollmentSource
{
    Self = 1,    // записался сам
    Teacher = 2, // записал учитель
    Parent = 3,  // записал родитель
    Series = 4,  // из записи на всю серию
}

public enum Attendance
{
    Unknown = 0, // учитель ещё не отметил
    Present = 1,
    Absent = 2,
}

/// <summary>Запись ученика на одно занятие и его посещаемость.</summary>
public class Enrollment
{
    public int Id { get; set; }

    public int LessonId { get; set; }
    public Lesson Lesson { get; set; } = null!;

    public int StudentId { get; set; }
    public Person Student { get; set; } = null!;

    public EnrollmentStatus Status { get; set; } = EnrollmentStatus.Active;
    public EnrollmentSource Source { get; set; }
    public Attendance Attendance { get; set; } = Attendance.Unknown;

    public DateTime CreatedAt { get; set; }
}
