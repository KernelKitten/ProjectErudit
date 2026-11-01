using Erudit.Domain.Notifications;
using Erudit.Infrastructure.Max;
using Erudit.Infrastructure.Persistence;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;

namespace Erudit.Infrastructure.Notifications;

/// <summary>Отправляет сообщения из очереди. Не вышло — повторяет позже, всего до пяти попыток.</summary>
public sealed class NotificationSender(
    EruditDbContext db,
    IMaxBotClient bot,
    TimeProvider time,
    ILogger<NotificationSender> logger)
{
    private const int BatchSize = 50;

    /// <summary>Через сколько пробовать снова после 1-й, 2-й, 3-й и 4-й неудачи; пятая неудача — окончательная.</summary>
    private static readonly TimeSpan[] RetryDelays =
    [
        TimeSpan.FromMinutes(1), TimeSpan.FromMinutes(5), TimeSpan.FromMinutes(15), TimeSpan.FromHours(1),
    ];

    /// <summary>MAX разрешает не больше 2 сообщений в секунду в один диалог.</summary>
    private static readonly TimeSpan SameDialogPause = TimeSpan.FromMilliseconds(600);

    /// <summary>Отправляет то, чему пора уйти. Возвращает, сколько сообщений отправлено.</summary>
    public async Task<int> SendDueAsync(CancellationToken ct = default)
    {
        var now = time.GetUtcNow().UtcDateTime;
        var due = await db.Notifications
            .Include(n => n.Recipient)
            .Where(n => n.Status == NotificationStatus.Pending && n.NextAttemptAt <= now)
            .OrderBy(n => n.Id)
            .Take(BatchSize)
            .ToListAsync(ct);

        var sent = 0;
        var dialogs = new HashSet<long>();
        foreach (var notification in due)
        {
            if (notification.ExpiresAt <= now)
            {
                notification.Status = NotificationStatus.Expired;
            }
            else if (notification.Recipient.MaxUserId is not { } maxUserId)
            {
                // Пока сообщение стояло в очереди, аккаунт MAX отвязали.
                notification.Status = NotificationStatus.Failed;
                notification.LastError = "У получателя нет MAX.";
            }
            else
            {
                if (!dialogs.Add(maxUserId))
                {
                    await Task.Delay(SameDialogPause, ct);
                }
                if (await TrySendAsync(notification, maxUserId, now, ct))
                {
                    sent++;
                }
            }
            // Сохраняем после каждого: при сбое на середине уже отправленное не уйдёт второй раз.
            await db.SaveChangesAsync(ct);
        }
        return sent;
    }

    private async Task<bool> TrySendAsync(Notification notification, long maxUserId, DateTime now, CancellationToken ct)
    {
        notification.Attempts++;
        try
        {
            await bot.SendMessageAsync(maxUserId, new MaxMessage(notification.Text), ct);
            notification.Status = NotificationStatus.Sent;
            notification.SentAt = now;
            notification.LastError = null;
            return true;
        }
        catch (Exception ex) when (ex is not OperationCanceledException || !ct.IsCancellationRequested)
        {
            // Таймаут HttpClient — тоже OperationCanceledException, но приложение при этом не останавливается.
            notification.LastError = ex.Message.Length > 300 ? ex.Message[..300] : ex.Message;
            if (notification.Attempts > RetryDelays.Length)
            {
                notification.Status = NotificationStatus.Failed;
                logger.LogError(ex, "Сообщение {Id} не отправлено после {Attempts} попыток", notification.Id, notification.Attempts);
            }
            else
            {
                notification.NextAttemptAt = now + RetryDelays[notification.Attempts - 1];
                logger.LogWarning(ex, "Сообщение {Id} не отправлено, попытка {Attempts}", notification.Id, notification.Attempts);
            }
            return false;
        }
    }
}

/// <summary>Фоновая служба: раз в несколько секунд отправляет сообщения из очереди.</summary>
public sealed class NotificationSenderService(
    IServiceScopeFactory scopes,
    ILogger<NotificationSenderService> logger) : BackgroundService
{
    private static readonly TimeSpan Interval = TimeSpan.FromSeconds(5);

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        using var timer = new PeriodicTimer(Interval);
        try
        {
            while (await timer.WaitForNextTickAsync(stoppingToken))
            {
                try
                {
                    // Своя «область» на каждый проход: свежий DbContext, как у отдельного запроса.
                    await using var scope = scopes.CreateAsyncScope();
                    await scope.ServiceProvider.GetRequiredService<NotificationSender>().SendDueAsync(stoppingToken);
                }
                catch (Exception ex) when (ex is not OperationCanceledException)
                {
                    // База недоступна и т. п. — следующий проход попробует снова; сайт продолжает работать.
                    logger.LogError(ex, "Ошибка отправки сообщений из очереди");
                }
            }
        }
        catch (OperationCanceledException) when (stoppingToken.IsCancellationRequested)
        {
            // Приложение останавливается.
        }
    }
}
