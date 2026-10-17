using System.Security.Cryptography;
using System.Text;
using Erudit.Domain.People;
using Erudit.Infrastructure.Persistence;
using Microsoft.EntityFrameworkCore;

namespace Erudit.Infrastructure.Auth;

/// <summary>
/// Запросы на вход по QR-коду: создать (страница входа), узнать статус (страница входа опрашивает),
/// забрать вход (адрес /auth/complete). Подтверждение в боте — в BotUpdateHandler.
/// </summary>
public sealed class LoginRequestService(IDbContextFactory<EruditDbContext> dbFactory, TimeProvider time)
{
    /// <summary>Префикс параметра ссылки на бота: ?start=login_ТОКЕН.</summary>
    public const string StartPayloadPrefix = "login_";

    /// <summary>Новый запрос. Секрет возвращается один раз — его хранит только открытая страница входа.</summary>
    public async Task<(LoginRequest Request, string Secret)> CreateAsync(string? deviceInfo, CancellationToken ct = default)
    {
        var secret = RandomToken(32);
        var request = new LoginRequest
        {
            // 16 случайных байт: угадать чужой токен практически невозможно.
            Token = RandomToken(16),
            SecretHash = Hash(secret),
            DeviceInfo = deviceInfo,
            CreatedAt = Now,
        };

        await using var db = await dbFactory.CreateDbContextAsync(ct);
        db.LoginRequests.Add(request);
        await db.SaveChangesAsync(ct);
        return (request, secret);
    }

    /// <summary>Текущий статус запроса; просроченный считается Expired.</summary>
    public async Task<LoginRequestState> GetStateAsync(int requestId, CancellationToken ct = default)
    {
        await using var db = await dbFactory.CreateDbContextAsync(ct);
        var request = await db.LoginRequests.AsNoTracking().SingleOrDefaultAsync(r => r.Id == requestId, ct);
        if (request is null || request.IsExpired(Now))
        {
            return LoginRequestState.Expired;
        }
        return request.Status switch
        {
            LoginRequestStatus.Confirmed => LoginRequestState.Confirmed,
            LoginRequestStatus.Rejected => LoginRequestState.Rejected,
            LoginRequestStatus.Used => LoginRequestState.Expired,
            _ => LoginRequestState.Waiting,
        };
    }

    /// <summary>
    /// Браузер забирает подтверждённый вход. Нужен секрет: одного токена из QR-кода недостаточно.
    /// Возвращает id человека или null.
    /// </summary>
    public async Task<int?> CompleteAsync(int requestId, string secret, CancellationToken ct = default)
    {
        await using var db = await dbFactory.CreateDbContextAsync(ct);
        var request = await db.LoginRequests.SingleOrDefaultAsync(r => r.Id == requestId, ct);
        if (request is null || !CryptographicOperations.FixedTimeEquals(
                Encoding.ASCII.GetBytes(request.SecretHash), Encoding.ASCII.GetBytes(Hash(secret))))
        {
            return null;
        }

        var personId = request.Use(Now);
        await db.SaveChangesAsync(ct);
        return personId;
    }

    private DateTime Now => time.GetUtcNow().UtcDateTime;

    /// <summary>Случайная строка из букв, цифр, «-» и «_» — такие символы разрешены в ссылке на бота.</summary>
    private static string RandomToken(int bytes) =>
        Convert.ToBase64String(RandomNumberGenerator.GetBytes(bytes)).TrimEnd('=').Replace('+', '-').Replace('/', '_');

    private static string Hash(string secret) => Convert.ToHexStringLower(SHA256.HashData(Encoding.UTF8.GetBytes(secret)));
}

public enum LoginRequestState
{
    Waiting,
    Confirmed,
    Rejected,
    Expired,
}
