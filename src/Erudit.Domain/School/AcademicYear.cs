namespace Erudit.Domain.School;

/// <summary>Учебный год, например «2026/27» с 01.09.2026 по 31.08.2027.</summary>
public class AcademicYear
{
    public int Id { get; set; }
    public string Name { get; set; } = "";
    public DateOnly StartDate { get; set; }
    public DateOnly EndDate { get; set; }

    public bool Contains(DateOnly date) => date >= StartDate && date <= EndDate;
}
