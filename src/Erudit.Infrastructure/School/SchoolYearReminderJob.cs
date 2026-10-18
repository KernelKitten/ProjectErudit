using Erudit.Domain;
using Erudit.Domain.People;
using Erudit.Infrastructure.Max;
using Erudit.Infrastructure.Persistence;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;

namespace Erudit.Infrastructure.School;

public sealed class SiteOptions
{
    public const string SectionName = "Site";

    /// <summary>Адрес сайта для ссылок в сообщениях бота (например, https://zapis.erudit23.ru). Пусто — без ссылки.</summary>
    public string BaseUrl { get; set; } = "";
}

/// <summary>
/// Напоминание о переводе школы на новый учебный год. Hangfire запускает задачу каждый день в 9:00,
/// а она сама решает, пора ли: только с 27 августа по 5 сентября и только если перевод ещё не сделан.
/// (Одним cron-выражением «27.08–05.09» не описать — диапазон переходит через границу месяца.)
/// </summary>
public sealed class SchoolYearReminderJob(
    EruditDbContext db,
    IMaxBotClient bot,
    TimeProvider time,
    IOptions<SiteOptions> site,
    ILogger<SchoolYearReminderJob> logger)
{
    public const string RecurringJobId = "school-year-reminder";

    public Task RunAsync(CancellationToken ct = default) =>
        RunForDateAsync(DateOnly.FromDateTime(SchoolTime.ToSchool(time.GetUtcNow().UtcDateTime)), ct);

    /// <summary>Отдельный метод с датой — чтобы тест мог проверить любой день, не дожидаясь августа.</summary>
    public async Task<int> RunForDateAsync(DateOnly today, CancellationToken ct = default)
    {
        var windowStart = new DateOnly(today.Year, 8, 27);
        var windowEnd = new DateOnly(today.Year, 9, 5);
        if (today < windowStart || today > windowEnd)
        {
            return 0;
        }

        // Перевод сделан, если у учебного года, начинающегося 1 сентября этого года, уже есть классы.
        var september = new DateOnly(today.Year, 9, 1);
        if (await db.SchoolClasses.AnyAsync(c => c.AcademicYear.StartDate == september, ct))
        {
            return 0;
        }

        var recipients = await db.People
            .Where(p => p.IsActive && p.MaxUserId != null
                        && p.Roles.Any(r => r.Role == Role.Admin || r.Role == Role.HeadTeacher))
            .Select(p => p.MaxUserId!.Value)
            .ToListAsync(ct);

        var link = string.IsNullOrWhiteSpace(site.Value.BaseUrl) ? "" : $"\n{site.Value.BaseUrl.TrimEnd('/')}/directories/years";
        var text = $"Не забудьте перевести школу на {september.Year}/{(september.Year + 1) % 100:D2} учебный год: " +
                   $"«Справочники» → «Учебные годы» → «Перевести школу на новый учебный год».{link}";
        foreach (var maxUserId in recipients)
        {
            try
            {
                await bot.SendMessageAsync(maxUserId, new MaxMessage(text), ct);
            }
            catch (HttpRequestException ex)
            {
                // Один недоступный адресат не должен мешать остальным.
                logger.LogWarning(ex, "Не удалось отправить напоминание о новом учебном годе MAX {MaxUserId}", maxUserId);
            }
        }
        logger.LogInformation("Напоминание о переводе на новый учебный год: отправлено {Count}", recipients.Count);
        return recipients.Count;
    }
}
