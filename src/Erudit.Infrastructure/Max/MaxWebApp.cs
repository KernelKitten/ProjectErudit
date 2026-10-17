using System.Net;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json.Nodes;

namespace Erudit.Infrastructure.Max;

/// <summary>
/// Проверка данных мини-приложения MAX на сервере.
///
/// initData — строка вида «auth_date=...&amp;hash=...&amp;user=%7B...%7D», которую MAX передаёт странице.
/// Подпись: secret = HMAC-SHA256(ключ "WebAppData", сообщение: токен бота);
/// hash = hex(HMAC-SHA256(ключ secret, сообщение: пары key=value без hash, по алфавиту, через \n)).
/// Без токена бота такую подпись не подделать — значит, user.id настоящий.
/// </summary>
public static class MaxWebApp
{
    /// <summary>Сколько живёт initData (рекомендация документации MAX — около часа).</summary>
    public static readonly TimeSpan MaxAge = TimeSpan.FromHours(1);

    /// <summary>Возвращает id пользователя MAX или null, если подпись неверна или данные устарели.</summary>
    public static long? ValidateInitData(string? initData, string botToken, DateTime nowUtc)
    {
        if (string.IsNullOrWhiteSpace(initData))
        {
            return null;
        }

        var pairs = new List<(string Key, string Value)>();
        foreach (var part in initData.Split('&', StringSplitOptions.RemoveEmptyEntries))
        {
            var eq = part.IndexOf('='); // делим по ПЕРВОМУ «=»: в значении он тоже может встретиться
            if (eq <= 0)
            {
                return null;
            }
            pairs.Add((WebUtility.UrlDecode(part[..eq]), WebUtility.UrlDecode(part[(eq + 1)..])));
        }

        var hashes = pairs.Where(p => p.Key == "hash").ToList();
        if (hashes.Count != 1)
        {
            return null;
        }

        var dataCheckString = string.Join('\n', pairs
            .Where(p => p.Key != "hash")
            .OrderBy(p => p.Key, StringComparer.Ordinal)
            .Select(p => $"{p.Key}={p.Value}"));

        var secret = HMACSHA256.HashData(Encoding.UTF8.GetBytes("WebAppData"), Encoding.UTF8.GetBytes(botToken));
        var expected = HMACSHA256.HashData(secret, Encoding.UTF8.GetBytes(dataCheckString));
        if (!HexEquals(expected, hashes[0].Value))
        {
            return null;
        }

        var values = pairs.ToDictionary(p => p.Key, p => p.Value);
        if (!values.TryGetValue("auth_date", out var authDateText) || !long.TryParse(authDateText, out var authDate)
            || nowUtc - DateTimeOffset.FromUnixTimeSeconds(authDate).UtcDateTime > MaxAge)
        {
            return null;
        }

        return values.TryGetValue("user", out var userJson)
            ? JsonNode.Parse(userJson)?["id"]?.GetValue<long>()
            : null;
    }

    /// <summary>
    /// Проверка номера из WebApp.requestContact(): hash = HMAC-SHA256 от строки
    /// «authDate=...\nphone=...\nuserId=...» (номер без «+»). Документация говорит «ключ — токен бота»,
    /// но для initData ключ выводится через "WebAppData", поэтому принимаем оба варианта: оба требуют токен.
    /// </summary>
    public static bool ValidateContact(string phone, string authDate, string hash, long userId, string botToken)
    {
        var data = Encoding.UTF8.GetBytes($"authDate={authDate}\nphone={phone.TrimStart('+')}\nuserId={userId}");
        var byToken = HMACSHA256.HashData(Encoding.UTF8.GetBytes(botToken), data);
        var secret = HMACSHA256.HashData(Encoding.UTF8.GetBytes("WebAppData"), Encoding.UTF8.GetBytes(botToken));
        var bySecret = HMACSHA256.HashData(secret, data);
        return HexEquals(byToken, hash) || HexEquals(bySecret, hash);
    }

    private static bool HexEquals(byte[] expected, string hex) =>
        CryptographicOperations.FixedTimeEquals(
            Encoding.ASCII.GetBytes(Convert.ToHexStringLower(expected)),
            Encoding.ASCII.GetBytes(hex.Trim().ToLowerInvariant()));
}
