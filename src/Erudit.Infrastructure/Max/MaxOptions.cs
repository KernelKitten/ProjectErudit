namespace Erudit.Infrastructure.Max;

/// <summary>
/// Настройки подключения к MAX. Заполняются из секции "Max" конфигурации
/// (appsettings.json + User Secrets).
/// </summary>
public sealed class MaxOptions
{
    public const string SectionName = "Max";

    /// <summary>Токен бота. Хранится только в User Secrets / переменных окружения.</summary>
    public string BotToken { get; set; } = "";

    /// <summary>Имя бота без @ — для ссылок https://max.ru/имя?start=...</summary>
    public string BotUsername { get; set; } = "";

    /// <summary>Адрес Bot API.</summary>
    public string ApiBaseUrl { get; set; } = "https://platform-api2.max.ru";

    /// <summary>
    /// Получать события через long polling (для разработки).
    /// В рабочей версии будет webhook.
    /// </summary>
    public bool UseLongPolling { get; set; }
}
