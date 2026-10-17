using System.Globalization;
using System.Text.Json;
using Erudit.Domain.Calendar;
using Erudit.Infrastructure.Persistence;
using Microsoft.EntityFrameworkCore;

namespace Erudit.Infrastructure.Calendar;

/// <summary>
/// Загружает производственный календарь (формат calendar.json из github.com/Duff072/holidays-calendar) в таблицу calendar_days.
/// Правила: дни, исправленные вручную (IsManual), не трогаем; в годах из файла приводим исключения
/// в точное соответствие файлу (добавляем, меняем, убираем лишние); другие годы не трогаем.
/// </summary>
public sealed class ProductionCalendarImporter(EruditDbContext db)
{
    /// <summary>С какого года загружаем — более старые годы для записи на занятия не нужны.</summary>
    public const int FromYear = 2026;

    public async Task<CalendarImportResult> ImportAsync(Stream json, CancellationToken ct = default)
    {
        var (holidays, nowork) = await ParseAsync(json, ct);
        var calendar = ProductionCalendar.ToExceptions(holidays, nowork, FromYear);
        if (calendar.Years.Count == 0)
        {
            throw new FormatException($"В файле нет данных за {FromYear} год и позже.");
        }

        var first = new DateOnly(calendar.Years[0], 1, 1);
        var last = new DateOnly(calendar.Years[^1], 12, 31);
        var existing = await db.CalendarDays.Where(d => d.Date >= first && d.Date <= last).ToDictionaryAsync(d => d.Date, ct);
        var wanted = calendar.Exceptions.ToDictionary(d => d.Date);
        var result = new CalendarImportResult { Years = calendar.Years };

        foreach (var day in wanted.Values)
        {
            if (!existing.TryGetValue(day.Date, out var current))
            {
                db.CalendarDays.Add(day);
                result.Added++;
            }
            else if (current.IsManual)
            {
                result.KeptManual++;
            }
            else if (current.IsWorkingDay != day.IsWorkingDay)
            {
                current.IsWorkingDay = day.IsWorkingDay;
                result.Updated++;
            }
        }

        // Исключения, которых больше нет в файле (например, перенос отменили), — убираем, кроме ручных.
        foreach (var stale in existing.Values.Where(d => !d.IsManual && !wanted.ContainsKey(d.Date)
                     && calendar.Years.Contains(d.Date.Year)))
        {
            db.CalendarDays.Remove(stale);
            result.Removed++;
        }

        await db.SaveChangesAsync(ct);
        return result;
    }

    /// <summary>Читает массивы holidays и nowork. Неправильный файл → FormatException с понятным текстом.</summary>
    private static async Task<(List<DateOnly> Holidays, List<DateOnly> Nowork)> ParseAsync(Stream json, CancellationToken ct)
    {
        JsonDocument doc;
        try
        {
            doc = await JsonDocument.ParseAsync(json, cancellationToken: ct);
        }
        catch (JsonException)
        {
            throw new FormatException("Файл не похож на calendar.json: это не JSON.");
        }

        using (doc)
        {
            if (!doc.RootElement.TryGetProperty("holidays", out var holidays) || holidays.ValueKind != JsonValueKind.Array)
            {
                throw new FormatException("В файле нет списка holidays — это не производственный календарь.");
            }
            var nowork = doc.RootElement.TryGetProperty("nowork", out var n) && n.ValueKind == JsonValueKind.Array ? Dates(n) : [];
            return (Dates(holidays), nowork);
        }

        static List<DateOnly> Dates(JsonElement array) => array.EnumerateArray()
            .Select(e => DateOnly.TryParseExact(e.GetString(), "yyyy-MM-dd", CultureInfo.InvariantCulture, DateTimeStyles.None, out var d)
                ? d
                : throw new FormatException($"Неверная дата в календаре: «{e}»."))
            .ToList();
    }
}

public sealed class CalendarImportResult
{
    public IReadOnlyList<int> Years { get; init; } = [];
    public int Added { get; set; }
    public int Updated { get; set; }
    public int Removed { get; set; }
    public int KeptManual { get; set; }

    public override string ToString() =>
        $"Годы {string.Join(", ", Years)}: добавлено {Added}, изменено {Updated}, удалено {Removed}, ручных правок сохранено {KeptManual}.";
}
