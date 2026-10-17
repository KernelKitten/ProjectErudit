namespace Erudit.Domain.People;

/// <summary>
/// Приведение телефона к одному виду +79991234567.
/// Нужно, чтобы «8 (999) 123-45-67» из Excel и «79991234567» из MAX считались одним номером.
/// </summary>
public static class PhoneNumber
{
    /// <summary>Возвращает номер в виде +7XXXXXXXXXX или null, если это не российский номер из 11 цифр.</summary>
    public static string? Normalize(string? input)
    {
        if (string.IsNullOrWhiteSpace(input))
        {
            return null;
        }

        var digits = new string(input.Where(char.IsAsciiDigit).ToArray());
        if (digits.Length == 10)
        {
            digits = "7" + digits;          // 9991234567 — без кода страны
        }
        else if (digits.Length == 11 && digits[0] == '8')
        {
            digits = "7" + digits[1..];     // 8 999... — по-старому через восьмёрку
        }

        return digits.Length == 11 && digits[0] == '7' ? "+" + digits : null;
    }
}
