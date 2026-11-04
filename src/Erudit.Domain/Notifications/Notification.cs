using System.Text.Json;
using Erudit.Domain.People;

namespace Erudit.Domain.Notifications;

public enum NotificationStatus
{
    Pending = 1, // ждёт отправки
    Sent = 2,    // отправлено
    Failed = 3,  // не удалось после всех попыток
    Expired = 4, // устарело раньше, чем удалось отправить (занятие уже началось)
}

/// <summary>Кнопка под сообщением: надпись и payload — что бот получит при нажатии (например, «skip:12:345»).</summary>
public sealed record NotificationButton(string Text, string Payload)
{
    public static string? ToJson(IReadOnlyCollection<NotificationButton> buttons) =>
        buttons.Count == 0 ? null : JsonSerializer.Serialize(buttons);

    public static IReadOnlyList<NotificationButton> FromJson(string? json) =>
        json is null ? [] : JsonSerializer.Deserialize<List<NotificationButton>>(json) ?? [];
}

/// <summary>
/// Сообщение бота. Одна таблица — и очередь (Pending), и журнал отправленного.
/// Строка сохраняется в одной транзакции с самим изменением (приём outbox, «исходящие»):
/// если MAX недоступен, сообщение не теряется, а отправляется позже.
/// </summary>
public class Notification
{
    public long Id { get; set; }

    public int RecipientId { get; set; }
    public Person Recipient { get; set; } = null!;

    public string Text { get; set; } = "";

    /// <summary>
    /// Ключ от дублей у сообщений по времени: «кому + что + о каком занятии или дне» (например, evening:15:20261112).
    /// Уникальный: ежеминутная задача не поставит одно и то же напоминание второй раз. У мгновенных — пусто.
    /// </summary>
    public string? Key { get; set; }

    /// <summary>Кнопки под сообщением (JSON, по одной в ряд) — см. <see cref="NotificationButton"/>. Пусто — без кнопок.</summary>
    public string? Buttons { get; set; }

    public NotificationStatus Status { get; set; } = NotificationStatus.Pending;

    public DateTime CreatedAt { get; set; }

    /// <summary>Когда пробовать отправить (следующая попытка — позже, если MAX не ответил).</summary>
    public DateTime NextAttemptAt { get; set; }

    /// <summary>После этого момента отправлять поздно: занятие уже началось.</summary>
    public DateTime ExpiresAt { get; set; }

    public int Attempts { get; set; }
    public DateTime? SentAt { get; set; }

    /// <summary>Последняя ошибка отправки.</summary>
    public string? LastError { get; set; }
}
