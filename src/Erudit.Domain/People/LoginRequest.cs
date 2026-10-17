namespace Erudit.Domain.People;

public enum LoginRequestStatus
{
    Pending = 1,   // QR-код показан, ждём подтверждения в боте
    Confirmed = 2, // человек нажал «Да» в боте, браузер ещё не забрал вход
    Rejected = 3,  // человек нажал «Нет»
    Used = 4,      // браузер получил cookie — запрос больше не действует
}

/// <summary>
/// Запрос на вход по QR-коду. В QR-коде — только <see cref="Token"/> (его может увидеть кто угодно,
/// например через плечо). Завершить вход может только тот браузер, который знает секрет:
/// он хранится только в открытой странице, а в базе — лишь его хэш (<see cref="SecretHash"/>).
/// </summary>
public class LoginRequest
{
    /// <summary>Сколько живёт QR-код до подтверждения.</summary>
    public static readonly TimeSpan ScanTimeout = TimeSpan.FromMinutes(3);

    /// <summary>Сколько после подтверждения браузер может забрать вход.</summary>
    public static readonly TimeSpan CompleteTimeout = TimeSpan.FromMinutes(2);

    public int Id { get; set; }

    /// <summary>Случайная строка в ссылке на бота: https://max.ru/бот?start=login_ТОКЕН.</summary>
    public string Token { get; set; } = "";

    /// <summary>SHA-256 секрета браузера (сам секрет в базе не хранится).</summary>
    public string SecretHash { get; set; } = "";

    /// <summary>«Windows, Chrome» — показываем в боте, чтобы человек узнал своё устройство.</summary>
    public string? DeviceInfo { get; set; }

    public DateTime CreatedAt { get; set; }
    public LoginRequestStatus Status { get; set; } = LoginRequestStatus.Pending;

    /// <summary>Кто отсканировал код (id в MAX) — запоминаем, пока человек проходит регистрацию.</summary>
    public long? MaxUserId { get; set; }

    public int? PersonId { get; set; }
    public Person? Person { get; set; }

    public DateTime? ConfirmedAt { get; set; }
    public DateTime? UsedAt { get; set; }

    public bool IsExpired(DateTime now) => Status switch
    {
        LoginRequestStatus.Pending => now > CreatedAt + ScanTimeout,
        LoginRequestStatus.Confirmed => now > ConfirmedAt + CompleteTimeout,
        _ => false,
    };

    /// <summary>Человек подтвердил вход в боте.</summary>
    public void Confirm(int personId, DateTime now)
    {
        if (Status != LoginRequestStatus.Pending || IsExpired(now))
        {
            throw new InvalidOperationException("Подтвердить можно только действующий запрос, ожидающий подтверждения.");
        }
        Status = LoginRequestStatus.Confirmed;
        PersonId = personId;
        ConfirmedAt = now;
    }

    /// <summary>Человек отказался входить.</summary>
    public void Reject()
    {
        if (Status == LoginRequestStatus.Pending)
        {
            Status = LoginRequestStatus.Rejected;
        }
    }

    /// <summary>
    /// Браузер забирает вход. Возвращает id человека или null, если забрать нельзя
    /// (не подтверждён, просрочен, уже использован). Повторно забрать вход нельзя.
    /// </summary>
    public int? Use(DateTime now)
    {
        if (Status != LoginRequestStatus.Confirmed || IsExpired(now))
        {
            return null;
        }
        Status = LoginRequestStatus.Used;
        UsedAt = now;
        return PersonId;
    }
}
