using System.Security.Cryptography;
using System.Text;
using System.Text.Json.Nodes;
using Erudit.Infrastructure.Max;

namespace Erudit.Tests.Max;

public class MaxContactTests
{
    private const string BotToken = "test-bot-token";
    private const long Sender = 78579095;

    /// <summary>Сообщение с контактом, как его присылает MAX (подпись считаем так же, как MAX).</summary>
    public static JsonObject ContactMessage(string phone, long owner, string botToken = BotToken, string? hash = null)
    {
        var vcf = $"BEGIN:VCARD\r\nVERSION:3.0\r\nTEL;TYPE=cell:{phone}\r\nFN:Тест\r\nEND:VCARD";
        hash ??= Convert.ToHexStringLower(HMACSHA256.HashData(Encoding.UTF8.GetBytes(botToken), Encoding.UTF8.GetBytes(vcf)));
        return new JsonObject
        {
            ["body"] = new JsonObject
            {
                ["attachments"] = new JsonArray
                {
                    new JsonObject
                    {
                        ["type"] = "contact",
                        ["payload"] = new JsonObject
                        {
                            ["vcf_info"] = vcf,
                            ["max_info"] = new JsonObject { ["user_id"] = owner },
                            ["hash"] = hash,
                        },
                    },
                },
            },
        };
    }

    [Fact]
    public void Подпись_верна__номер_приведён_к_виду_плюс7()
    {
        Assert.Equal("+79991234567", MaxContact.TryGetVerifiedPhone(ContactMessage("79991234567", Sender), Sender, BotToken));
    }

    [Fact]
    public void Подпись_чужим_токеном__номер_не_принимается()
    {
        var message = ContactMessage("79991234567", Sender, botToken: "другой-токен");

        Assert.Null(MaxContact.TryGetVerifiedPhone(message, Sender, BotToken));
    }

    [Fact]
    public void Подделанная_подпись__номер_не_принимается()
    {
        var message = ContactMessage("79991234567", Sender, hash: new string('0', 64));

        Assert.Null(MaxContact.TryGetVerifiedPhone(message, Sender, BotToken));
    }

    [Fact]
    public void Чужой_контакт__номер_не_принимается()
    {
        Assert.Null(MaxContact.TryGetVerifiedPhone(ContactMessage("79991234567", owner: 12345), Sender, BotToken));
    }

    [Fact]
    public void Сообщение_без_контакта__null()
    {
        var message = new JsonObject { ["body"] = new JsonObject { ["text"] = "привет" } };

        Assert.Null(MaxContact.TryGetVerifiedPhone(message, Sender, BotToken));
    }
}
