using System.Text.Encodings.Web;
using System.Text.Json;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;

namespace Erudit.Infrastructure.Max;

/// <summary>
/// Фоновая служба: в бесконечном цикле спрашивает у MAX новые события (long polling)
/// и передаёт их в <see cref="BotUpdateHandler"/>. Работает, пока запущено приложение.
/// </summary>
public sealed class MaxLongPollingService(
    IMaxBotClient bot,
    IServiceScopeFactory scopes,
    ILogger<MaxLongPollingService> logger) : BackgroundService
{
    private const int PollTimeoutSeconds = 30;

    // По умолчанию кириллица в JSON экранируется как Т; для лога удобнее читаемый текст.
    private static readonly JsonSerializerOptions ReadableJson = new()
    {
        Encoder = JavaScriptEncoder.UnsafeRelaxedJsonEscaping,
    };

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        // Сначала проверяем, что бот доступен. Не вышло (нет сети, неверный токен, не доверяем сертификату MAX) —
        // пишем ошибку и пробуем снова через минуту. Исключение отсюда остановило бы не только бота, а весь сайт.
        while (true)
        {
            try
            {
                var me = await bot.GetMeAsync(stoppingToken);
                logger.LogInformation("Бот MAX подключён: {Name} (@{Username}), id {Id}", me.Name, me.Username, me.UserId);
                break;
            }
            catch (OperationCanceledException) when (stoppingToken.IsCancellationRequested)
            {
                return;
            }
            catch (Exception ex)
            {
                logger.LogError(ex, "Бот MAX недоступен, повтор через минуту");
                await Task.Delay(TimeSpan.FromMinutes(1), stoppingToken);
            }
        }

        long? marker = null;
        while (!stoppingToken.IsCancellationRequested)
        {
            try
            {
                var page = await bot.GetUpdatesAsync(marker, PollTimeoutSeconds, stoppingToken);
                foreach (var update in page.Updates)
                {
                    // Полный JSON — на уровне Debug (включён в appsettings.Development.json):
                    // в нём персональные данные, на рабочем сервере не пишем.
                    logger.LogDebug("Событие MAX: {Json}", update.ToJsonString(ReadableJson));

                    // Своя «область» (scope) на каждое событие: свой DbContext, как у отдельного HTTP-запроса.
                    await using var scope = scopes.CreateAsyncScope();
                    var handler = scope.ServiceProvider.GetRequiredService<BotUpdateHandler>();
                    try
                    {
                        await handler.HandleAsync(update, stoppingToken);
                    }
                    catch (Exception ex) when (ex is not OperationCanceledException)
                    {
                        // Ошибка в одном событии не должна останавливать бота.
                        logger.LogError(ex, "Ошибка обработки события MAX {Type}", update["update_type"]);
                    }
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
}
