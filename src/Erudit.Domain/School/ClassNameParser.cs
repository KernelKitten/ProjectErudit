using System.Text.RegularExpressions;

namespace Erudit.Domain.School;

/// <summary>
/// Разбирает строку вроде «1а, 1б, 2а, 5а-в, 10, 11» в список классов (параллель + буква).
/// «5а-в» — диапазон букв: 5а, 5б, 5в. Ошибки собираются все сразу, чтобы показать человеку весь список.
/// </summary>
public static partial class ClassNameParser
{
    /// <summary>Буквы классов по порядку (без «ё» и «й» — ими классы не называют).</summary>
    private const string Alphabet = "абвгдежзиклмнопрстуфхцчшщэюя";

    public static ClassNameParseResult Parse(string? input)
    {
        var classes = new List<(int Grade, string Letter)>();
        var errors = new List<string>();

        foreach (var token in (input ?? "").Split([',', ';', ' ', '\n', '\t'], StringSplitOptions.RemoveEmptyEntries))
        {
            var match = Token().Match(token.ToLowerInvariant().Replace('ё', 'е').Replace('–', '-').Replace('—', '-'));
            if (!match.Success)
            {
                errors.Add($"«{token}» — не похоже на класс (пример: 5а, 5а-в, 10)");
                continue;
            }

            var grade = int.Parse(match.Groups["grade"].Value);
            if (grade > 11)
            {
                errors.Add($"«{token}» — параллель {grade} (бывают 0–11)");
                continue;
            }

            var from = match.Groups["from"].Value;
            var to = match.Groups["to"].Value;
            if (to.Length == 0)
            {
                classes.Add((grade, from)); // одна буква или класс без буквы («10»)
                continue;
            }

            var start = Alphabet.IndexOf(from, StringComparison.Ordinal);
            var end = Alphabet.IndexOf(to, StringComparison.Ordinal);
            if (start < 0 || end < 0 || end < start)
            {
                errors.Add($"«{token}» — непонятный диапазон букв");
                continue;
            }
            for (var i = start; i <= end; i++)
            {
                classes.Add((grade, Alphabet[i].ToString()));
            }
        }

        return new ClassNameParseResult(classes.Distinct().OrderBy(c => c.Grade).ThenBy(c => c.Letter).ToList(), errors);
    }

    // Параллель 1–2 цифры, потом необязательная буква, потом необязательный диапазон «-буква».
    [GeneratedRegex(@"^(?<grade>\d{1,2})(?<from>[а-я]?)(-(?<to>[а-я]))?$")]
    private static partial Regex Token();
}

public sealed record ClassNameParseResult(IReadOnlyList<(int Grade, string Letter)> Classes, IReadOnlyList<string> Errors);
