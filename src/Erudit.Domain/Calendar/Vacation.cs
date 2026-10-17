namespace Erudit.Domain.Calendar;

/// <summary>
/// Каникулы. Только подсвечиваются в календаре — занятия на каникулах разрешены.
/// Можно ограничить параллелями: например, дополнительные каникулы только для 1-х классов.
/// </summary>
public class Vacation
{
    public int Id { get; set; }
    public string Name { get; set; } = "";
    public DateOnly StartDate { get; set; }
    public DateOnly EndDate { get; set; }

    /// <summary>С какой параллели действуют (пусто — с 1-й).</summary>
    public int? GradeFrom { get; set; }

    /// <summary>По какую параллель действуют (пусто — по 11-ю).</summary>
    public int? GradeTo { get; set; }

    public bool AppliesTo(DateOnly date, int grade) =>
        date >= StartDate && date <= EndDate
        && grade >= (GradeFrom ?? 1)
        && grade <= (GradeTo ?? 11);
}
