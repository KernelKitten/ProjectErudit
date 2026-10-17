using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;

namespace Erudit.Infrastructure.Calendar;

public sealed class CalendarOptions
{
    public const string SectionName = "Calendar";

    /// <summary>Откуда скачивать производственный календарь.</summary>
    public string Url { get; set; } = "https://raw.githubusercontent.com/Duff072/holidays-calendar/master/json/calendar.json";
}

/// <summary>
/// Фоновая задача Hangfire «скачать производственный календарь с GitHub и загрузить в базу».
/// Hangfire сам создаёт объект этого класса через DI и вызывает RunAsync по расписанию (раз в неделю);
/// администратор может запустить её и вручную — кнопкой на странице календаря.
/// </summary>
public sealed class ProductionCalendarJob(
    HttpClient http,
    IOptions<CalendarOptions> options,
    ProductionCalendarImporter importer,
    ILogger<ProductionCalendarJob> logger)
{
    public const string RecurringJobId = "production-calendar";

    public async Task<CalendarImportResult> RunAsync(CancellationToken ct = default)
    {
        await using var json = await http.GetStreamAsync(options.Value.Url, ct);
        var result = await importer.ImportAsync(json, ct);
        logger.LogInformation("Производственный календарь загружен с GitHub. {Result}", result);
        return result;
    }
}
