namespace Erudit.Domain.People;

/// <summary>
/// ФИО из одной строки: «Иванова Мария Петровна» → фамилия, имя, отчество.
/// Первое слово — фамилия, второе — имя, всё остальное — отчество
/// (бывают отчества из двух слов: «Рашид кызы»). Отчества может не быть.
/// </summary>
public sealed record PersonName(string LastName, string FirstName, string? MiddleName)
{
    public static PersonName? TryParse(string? text)
    {
        var words = (text ?? "")
            .Split([' ', '\t', ' '], StringSplitOptions.RemoveEmptyEntries)
            .Select(Capitalize)
            .ToArray();
        if (words.Length < 2)
        {
            return null; // нужны хотя бы фамилия и имя
        }
        return new PersonName(words[0], words[1], words.Length > 2 ? string.Join(' ', words[2..]) : null);
    }

    public string FullName => MiddleName is null ? $"{LastName} {FirstName}" : $"{LastName} {FirstName} {MiddleName}";

    /// <summary>Одинаковые ли это ФИО, без учёта регистра и «ё/е».</summary>
    public bool SameAs(string lastName, string firstName, string? middleName) =>
        Normalize(FullName) == Normalize(new PersonName(lastName, firstName, middleName).FullName);

    private static string Normalize(string s) => s.ToLowerInvariant().Replace('ё', 'е');

    /// <summary>«иВАНОВА» → «Иванова», «петров-водкин» → «Петров-Водкин». Слово «кызы»/«оглы» оставляем маленьким.</summary>
    private static string Capitalize(string word)
    {
        var lower = word.ToLowerInvariant();
        if (lower is "кызы" or "оглы")
        {
            return lower;
        }
        return string.Join('-', lower.Split('-').Select(p => p.Length == 0 ? p : char.ToUpperInvariant(p[0]) + p[1..]));
    }
}
