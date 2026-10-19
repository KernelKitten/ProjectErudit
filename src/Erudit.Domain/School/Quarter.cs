namespace Erudit.Domain.School;

/// <summary>
/// Учебная четверть: «1-я четверть 2026/27, с 01.09 по 25.10».
/// Нужна для «повторять до конца четверти» и отчётов «за четверть».
/// </summary>
public class Quarter
{
    public int Id { get; set; }

    public int AcademicYearId { get; set; }
    public AcademicYear AcademicYear { get; set; } = null!;

    /// <summary>Номер четверти 1–4.</summary>
    public int Number { get; set; }

    public DateOnly StartDate { get; set; }
    public DateOnly EndDate { get; set; }

    public string Name => $"{Number}-я четверть";

    public bool Contains(DateOnly date) => date >= StartDate && date <= EndDate;
}

/// <summary>Правила для четвертей — чистая логика, проверяется тестами.</summary>
public static class QuarterRules
{
    /// <summary>
    /// Проверяет четверть перед сохранением. Возвращает текст ошибки или null, если всё в порядке.
    /// <paramref name="others"/> — остальные четверти того же учебного года (без проверяемой).
    /// </summary>
    public static string? Validate(int number, DateOnly start, DateOnly end, AcademicYear year, IEnumerable<Quarter> others)
    {
        if (number is < 1 or > 4)
        {
            return "Номер четверти — от 1 до 4.";
        }
        if (end < start)
        {
            return "Четверть заканчивается раньше, чем начинается.";
        }
        if (!year.Contains(start) || !year.Contains(end))
        {
            return $"Четверть должна быть внутри учебного года {year.Name} " +
                   $"({year.StartDate:dd.MM.yyyy} — {year.EndDate:dd.MM.yyyy}).";
        }
        foreach (var other in others.OrderBy(q => q.Number))
        {
            if (other.Number == number)
            {
                return $"{other.Name} в {year.Name} уже есть.";
            }
            if (start <= other.EndDate && end >= other.StartDate)
            {
                return $"Пересекается с {other.Number}-й четвертью ({other.StartDate:dd.MM} — {other.EndDate:dd.MM}).";
            }
            // Пересечений нет — осталось проверить порядок: 1-я раньше 2-й и т. д.
            if ((other.Number < number && other.StartDate > end) || (other.Number > number && other.StartDate < start))
            {
                return $"Четверти должны идти по порядку: {other.Number}-я четверть — {other.StartDate:dd.MM} — {other.EndDate:dd.MM}.";
            }
        }
        return null;
    }
}
