using System.Net;
using System.Security.Cryptography;
using System.Text;
using Erudit.Infrastructure.Max;

namespace Erudit.Tests.Max;

public class MaxWebAppTests
{
    private const string BotToken = "test-bot-token";
    private static readonly DateTime Now = new(2026, 10, 17, 12, 0, 0, DateTimeKind.Utc);

    /// <summary>initData так, как его подписывает MAX.</summary>
    private static string InitData(long userId, DateTime authDate, string botToken = BotToken)
    {
        var fields = new SortedDictionary<string, string>(StringComparer.Ordinal)
        {
            ["auth_date"] = new DateTimeOffset(authDate).ToUnixTimeSeconds().ToString(),
            ["query_id"] = "AAE-test=",
            ["user"] = $$"""{"id":{{userId}},"first_name":"Иван","last_name":"Петров"}""",
        };
        var dataCheckString = string.Join('\n', fields.Select(f => $"{f.Key}={f.Value}"));
        var secret = HMACSHA256.HashData(Encoding.UTF8.GetBytes("WebAppData"), Encoding.UTF8.GetBytes(botToken));
        var hash = Convert.ToHexStringLower(HMACSHA256.HashData(secret, Encoding.UTF8.GetBytes(dataCheckString)));

        return string.Join('&', fields.Select(f => $"{f.Key}={WebUtility.UrlEncode(f.Value)}")) + $"&hash={hash}";
    }

    [Fact]
    public void Верная_подпись__возвращает_id_пользователя()
    {
        Assert.Equal(185986309, MaxWebApp.ValidateInitData(InitData(185986309, Now.AddMinutes(-5)), BotToken, Now));
    }

    [Fact]
    public void Подменённый_пользователь__отказ()
    {
        var initData = InitData(185986309, Now).Replace("185986309", "111");

        Assert.Null(MaxWebApp.ValidateInitData(initData, BotToken, Now));
    }

    [Fact]
    public void Подпись_другим_ботом__отказ()
    {
        Assert.Null(MaxWebApp.ValidateInitData(InitData(1, Now, botToken: "другой-бот"), BotToken, Now));
    }

    [Fact]
    public void Данные_старше_часа__отказ()
    {
        Assert.Null(MaxWebApp.ValidateInitData(InitData(1, Now.AddHours(-2)), BotToken, Now));
    }

    [Theory]
    [InlineData(null)]
    [InlineData("")]
    [InlineData("user=%7B%7D")]               // без подписи
    [InlineData("hash=1&hash=2&auth_date=1")] // две подписи
    public void Испорченные_данные__отказ(string? initData)
    {
        Assert.Null(MaxWebApp.ValidateInitData(initData, BotToken, Now));
    }

    [Fact]
    public void Номер_из_requestContact__проверка_подписи()
    {
        const long userId = 185986309;
        var data = Encoding.UTF8.GetBytes($"authDate=1792263798\nphone=79991234567\nuserId={userId}");
        var hash = Convert.ToHexStringLower(HMACSHA256.HashData(Encoding.UTF8.GetBytes(BotToken), data));

        Assert.True(MaxWebApp.ValidateContact("+79991234567", "1792263798", hash, userId, BotToken));
        Assert.False(MaxWebApp.ValidateContact("+79990000000", "1792263798", hash, userId, BotToken)); // чужой номер
        Assert.False(MaxWebApp.ValidateContact("+79991234567", "1792263798", hash, userId + 1, BotToken)); // чужой аккаунт
        Assert.False(MaxWebApp.ValidateContact("+79991234567", "1792263798", hash, userId, "другой-бот")); // чужой бот
    }
}
