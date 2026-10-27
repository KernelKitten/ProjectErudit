using System.Security.Cryptography.X509Certificates;
using Erudit.Infrastructure.Auth;
using Erudit.Infrastructure.Calendar;
using Hangfire;
using Hangfire.PostgreSql;
using Erudit.Infrastructure.Max;
using Erudit.Infrastructure.People;
using Erudit.Infrastructure.School;
using Erudit.Infrastructure.Persistence;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;
using Microsoft.Extensions.Options;

namespace Erudit.Infrastructure;

/// <summary>
/// Регистрация сервисов слоя Infrastructure в контейнере зависимостей.
/// Erudit.Web вызывает один метод — AddInfrastructure — и не знает подробностей.
/// </summary>
public static class DependencyInjection
{
    public static IServiceCollection AddInfrastructure(this IServiceCollection services, IConfiguration configuration)
    {
        var connectionString = configuration.GetConnectionString("Erudit")
            ?? throw new InvalidOperationException(
                "Не задана строка подключения ConnectionStrings:Erudit. Добавьте её в User Secrets.");

        // Фабрика контекстов: в Blazor Server страница живёт долго (пока открыта вкладка),
        // а DbContext рассчитан на короткую работу. Поэтому страницы берут свежий контекст
        // из фабрики на каждую операцию: await using var db = await dbFactory.CreateDbContextAsync();
        services.AddDbContextFactory<EruditDbContext>(options => options
            .UseNpgsql(connectionString)
            // Имена таблиц и колонок в стиле PostgreSQL: lesson_periods, starts_at.
            .UseSnakeCaseNamingConvention());
        // Для кода вне страниц (фоновые задачи, старт приложения) — обычный контекст на время запроса/области.
        services.AddScoped(sp => sp.GetRequiredService<IDbContextFactory<EruditDbContext>>().CreateDbContext());

        services.AddOptions<MaxOptions>()
            .Bind(configuration.GetSection(MaxOptions.SectionName))
            .Validate(o => !string.IsNullOrWhiteSpace(o.BotToken),
                "Не задан токен бота MAX (Max:BotToken). Добавьте его в User Secrets.")
            .ValidateOnStart();

        // Типизированный HttpClient: фабрика сама управляет соединениями,
        // а адрес и заголовок авторизации задаются один раз здесь.
        services.AddHttpClient<IMaxBotClient, MaxBotClient>((sp, http) =>
        {
            var options = sp.GetRequiredService<IOptions<MaxOptions>>().Value;
            http.BaseAddress = new Uri(options.ApiBaseUrl.TrimEnd('/') + "/");
            http.DefaultRequestHeaders.Add("Authorization", options.BotToken);
            // Long polling держит запрос до 30 с — оставляем запас.
            http.Timeout = TimeSpan.FromSeconds(60);
        })
        // Сертификат MAX выдан Russian Trusted Root CA — доверяем ему только в этом клиенте.
        .ConfigurePrimaryHttpMessageHandler(() => new SocketsHttpHandler
        {
            SslOptions =
            {
                RemoteCertificateValidationCallback = (_, certificate, chain, errors) =>
                    MaxCertificateTrust.Validate(certificate as X509Certificate2, chain, errors),
            },
        });

        // Производственный календарь: загрузчик и фоновая задача.
        services.AddOptions<CalendarOptions>().Bind(configuration.GetSection(CalendarOptions.SectionName));
        services.AddScoped<ProductionCalendarImporter>();
        services.AddHttpClient<ProductionCalendarJob>();

        // Hangfire — фоновые задачи по расписанию. Задачи хранятся в той же базе, в отдельной схеме "hangfire":
        // если сервер перезапустится, расписание и история не потеряются.
        services.AddHangfire(config => config
            .UseRecommendedSerializerSettings()
            .UsePostgreSqlStorage(storage => storage.UseNpgsqlConnection(connectionString),
                new PostgreSqlStorageOptions { SchemaName = "hangfire", PrepareSchemaIfNecessary = true }));
        services.AddHangfireServer();

        services.TryAddSingleton(TimeProvider.System);
        services.AddScoped<BotUpdateHandler>();
        services.AddScoped<PersonRegistration>();
        services.AddScoped<TeacherService>();
        services.AddScoped<StudentService>();
        services.AddScoped<DataCheckService>();
        services.AddScoped<SchoolYearTransition>();
        services.AddScoped<Lessons.LessonService>();
        services.AddScoped<Lessons.TeacherWeekService>();
        services.AddScoped<Lessons.LessonFormService>();
        services.AddScoped<SchoolYearReminderJob>();
        services.AddOptions<SiteOptions>().Bind(configuration.GetSection(SiteOptions.SectionName));
        services.AddSingleton<LoginRequestService>();

        var useLongPolling = configuration.GetValue<bool>($"{MaxOptions.SectionName}:{nameof(MaxOptions.UseLongPolling)}");
        if (useLongPolling)
        {
            services.AddHostedService<MaxLongPollingService>();
        }

        return services;
    }

    /// <summary>Расписание фоновых задач. Вызывается один раз при старте; AddOrUpdate не создаёт дублей.</summary>
    public static void ScheduleRecurringJobs(this IServiceProvider services)
    {
        var jobs = services.GetRequiredService<IRecurringJobManager>();
        // Каждый понедельник в 04:00 по школьному времени.
        jobs.AddOrUpdate<ProductionCalendarJob>(ProductionCalendarJob.RecurringJobId,
            job => job.RunAsync(CancellationToken.None), "0 4 * * 1",
            new RecurringJobOptions { TimeZone = Erudit.Domain.SchoolTime.Zone });
        // Каждый день в 9:00; сама задача решает, пора ли напоминать (27.08–05.09 и перевод не сделан).
        jobs.AddOrUpdate<SchoolYearReminderJob>(SchoolYearReminderJob.RecurringJobId,
            job => job.RunAsync(CancellationToken.None), "0 9 * * *",
            new RecurringJobOptions { TimeZone = Erudit.Domain.SchoolTime.Zone });
    }
}
