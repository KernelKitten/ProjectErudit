namespace Erudit.Domain.School;

/// <summary>Предмет: «Математика».</summary>
public class Subject
{
    public int Id { get; set; }
    public string Name { get; set; } = "";

    /// <summary>Как в школьном расписании: «Матем», «Англ.яз».</summary>
    public string? ShortName { get; set; }

    public bool IsActive { get; set; } = true;
}

/// <summary>Кабинет: «86», «Естеств», «Спортзал».</summary>
public class Room
{
    public int Id { get; set; }
    public string Name { get; set; } = "";
    public int? Floor { get; set; }
    public bool IsActive { get; set; } = true;
}

/// <summary>
/// Урок по расписанию звонков: номер (0 — до первого урока, для начальной школы) и время.
/// Одно расписание звонков на всю школу.
/// </summary>
public class LessonPeriod
{
    /// <summary>Номер урока 0–9. Он же ключ таблицы.</summary>
    public int Number { get; set; }
    public TimeOnly Start { get; set; }
    public TimeOnly End { get; set; }

    public int DurationMinutes => (int)(End - Start).TotalMinutes;
}
