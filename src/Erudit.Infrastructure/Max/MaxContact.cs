using System.Security.Cryptography;
using System.Text;
using System.Text.Json.Nodes;
using System.Text.RegularExpressions;
using Erudit.Domain.People;

namespace Erudit.Infrastructure.Max;

/// <summary>
/// Контакт, присланный кнопкой «Поделиться номером» (вложение type = "contact").
/// MAX кладёт в него визитку vcf_info, сведения о пользователе max_info и подпись hash.
/// Подпись = HMAC-SHA256(ключ: токен бота, сообщение: vcf_info). Подделать её без токена нельзя,
/// поэтому номер с верной подписью действительно привязан к аккаунту MAX этого человека.
/// </summary>
public static partial class MaxContact
{
    /// <summary>
    /// Достаёт из сообщения проверенный номер телефона (в виде +7...).
    /// Возвращает null, если контакта нет, подпись неверна или контакт чужой.
    /// </summary>
    /// <param name="message">Объект message из события message_created.</param>
    /// <param name="senderId">Кто прислал сообщение: делиться можно только своим номером.</param>
    public static string? TryGetVerifiedPhone(JsonObject message, long senderId, string botToken)
    {
        var contact = message["body"]?["attachments"]?.AsArray()
            .OfType<JsonObject>()
            .FirstOrDefault(a => a["type"]?.GetValue<string>() == "contact")?["payload"];
        var vcf = contact?["vcf_info"]?.GetValue<string>();
        var hash = contact?["hash"]?.GetValue<string>();
        if (vcf is null || hash is null)
        {
            return null;
        }

        // Документация MAX: перед проверкой превратить «\r\n», записанные текстом, в настоящие переводы строк.
        vcf = vcf.Replace("\\r\\n", "\r\n");
        if (!IsSignatureValid(vcf, hash, botToken))
        {
            return null;
        }

        // Контакт должен принадлежать отправителю (не пересланная визитка одноклассника).
        var owner = contact?["max_info"]?["user_id"]?.GetValue<long>();
        if (owner is not null && owner != senderId)
        {
            return null;
        }

        return PhoneNumber.Normalize(TelLine().Match(vcf) is { Success: true } m ? m.Groups[1].Value : null);
    }

    public static bool IsSignatureValid(string vcf, string hash, string botToken)
    {
        var expected = HMACSHA256.HashData(Encoding.UTF8.GetBytes(botToken), Encoding.UTF8.GetBytes(vcf));
        // Сравнение «за постоянное время»: не даёт подбирать подпись по времени ответа.
        return CryptographicOperations.FixedTimeEquals(
            Encoding.ASCII.GetBytes(Convert.ToHexStringLower(expected)),
            Encoding.ASCII.GetBytes(hash.ToLowerInvariant()));
    }

    /// <summary>Строка визитки вида «TEL;TYPE=cell:79991234567».</summary>
    [GeneratedRegex(@"^TEL[^:]*:(.+)$", RegexOptions.Multiline)]
    private static partial Regex TelLine();
}
