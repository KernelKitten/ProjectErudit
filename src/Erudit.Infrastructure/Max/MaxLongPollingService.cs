using System.Text.Encodings.Web;
using System.Text.Json;
using System.Text.Json.Nodes;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;

namespace Erudit.Infrastructure.Max;

/// <summary>
/// Фоновая служба: в бесконечном цикле спрашивает у MAX новые события (long polling)
/// и обрабатывает их. Работает, пока запущено приложение.
///
/// Этап 0 — проба: бот здоровается и показывает параметр ?start=.
/// Все события пишутся в лог целиком, чтобы изучить их реальный формат.
/// </summary>
public sealed class MaxLongPollingService(MaxBotClient bot, ILogger<MaxLongPollingService> logger) : BackgroundService
{
    private const int PollTimeoutSeconds = 30;

    // По умолчанию кириллица в JSON экранируется как Т; для лога удобнее читаемый текст.
    private static readonly JsonSerializerOptions ReadableJson = new()
    {
        Encoder = JavaScriptEncoder.UnsafeRelaxedJsonEscaping,
    };

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        var me = await bot.GetMeAsync(stoppingToken);
        logger.LogInformation("Бот MAX подключён: {Name} (@{Username}), id {Id}", me.Name, me.Username, me.UserId);

        long? marker = null;
        while (!stoppingToken.IsCancellationRequested)
        {
            try
            {
                var page = await bot.GetUpdatesAsync(marker, PollTimeoutSeconds, stoppingToken);
                foreach (var update in page.Updates)
                {
                    await HandleUpdateAsync(update, stoppingToken);
                }
                marker = page.Marker ?? marker;
            }
            catch (OperationCanceledException) when (stoppingToken.IsCancellationRequested)
            {
                break; // приложение останавливается — выходим из цикла
            }
            catch (Exception ex)
            {
                // Сеть пропала или MAX ответил ошибкой — ждём и пробуем снова, а не падаем.
                logger.LogWarning(ex, "Ошибка при получении событий MAX, повтор через 5 секунд");
                await Task.Delay(TimeSpan.FromSeconds(5), stoppingToken);
            }
        }
    }

    private async Task HandleUpdateAsync(JsonObject update, CancellationToken ct)
    {
        var type = update["update_type"]?.GetValue<string>();
        // Полный JSON в логе — только для изучения формата на этапе 0:
        // в нём персональные данные (имя, id), в рабочей версии так не делаем.
        logger.LogInformation("Событие MAX {Type}: {Json}", type, update.ToJsonString(ReadableJson));

        switch (type)
        {
            // Пользователь нажал «Начать» или открыл ссылку https://max.ru/<бот>?start=<payload>
            case "bot_started":
            {
                var userId = update["user"]?["user_id"]?.GetValue<long>();
                var name = update["user"]?["first_name"]?.GetValue<string>();
                var payload = update["payload"]?.GetValue<string>();
                if (userId is null) return;

                var text = payload is null
                    ? $"Привет, {name}! Я бот для записи на дополнительные занятия."
                    : $"Привет, {name}! Параметр start: {payload}";
                await bot.SendMessageToUserAsync(userId.Value, text, ct);
                break;
            }

            // Пользователь написал боту сообщение
            case "message_created":
            {
                var userId = update["message"]?["sender"]?["user_id"]?.GetValue<long>();
                var name = update["message"]?["sender"]?["first_name"]?.GetValue<string>();
                var text = update["message"]?["body"]?["text"]?.GetValue<string>();
                if (userId is null) return;

                await bot.SendMessageToUserAsync(userId.Value, $"Привет, {name}! Вы написали: {text}", ct);
                break;
            }
        }
    }
}
