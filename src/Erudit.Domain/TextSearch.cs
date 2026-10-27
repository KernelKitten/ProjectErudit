namespace Erudit.Domain;

/// <summary>
/// Поиск по тексту так, как ищут люди: без учёта регистра и разницы «е/ё»
/// («семенов» находит «Семёнов»). Несколько слов — должны найтись все, в любом порядке
/// («мар иван» находит «Иванова Мария»).
/// </summary>
public static class TextSearch
{
    public static bool Matches(string text, string? query)
    {
        var words = Normalize(query).Split(' ', StringSplitOptions.RemoveEmptyEntries);
        if (words.Length == 0)
        {
            return true; // пустой поиск — подходит всё
        }
        var normalized = Normalize(text);
        return words.All(normalized.Contains);
    }

    public static string Normalize(string? s) => (s ?? "").Trim().ToLowerInvariant().Replace('ё', 'е');
}
