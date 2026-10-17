using Erudit.Infrastructure.Max;
using Erudit.Infrastructure.Persistence;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
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

        services.AddDbContext<EruditDbContext>(options => options
            .UseNpgsql(connectionString)
            // Имена таблиц и колонок в стиле PostgreSQL: lesson_periods, starts_at.
            .UseSnakeCaseNamingConvention());

        services.AddOptions<MaxOptions>()
            .Bind(configuration.GetSection(MaxOptions.SectionName))
            .Validate(o => !string.IsNullOrWhiteSpace(o.BotToken),
                "Не задан токен бота MAX (Max:BotToken). Добавьте его в User Secrets.")
            .ValidateOnStart();

        // Типизированный HttpClient: фабрика сама управляет соединениями,
        // а адрес и заголовок авторизации задаются один раз здесь.
        services.AddHttpClient<MaxBotClient>((sp, http) =>
        {
            var options = sp.GetRequiredService<IOptions<MaxOptions>>().Value;
            http.BaseAddress = new Uri(options.ApiBaseUrl.TrimEnd('/') + "/");
            http.DefaultRequestHeaders.Add("Authorization", options.BotToken);
            // Long polling держит запрос до 30 с — оставляем запас.
            http.Timeout = TimeSpan.FromSeconds(60);
        });

        var useLongPolling = configuration.GetValue<bool>($"{MaxOptions.SectionName}:{nameof(MaxOptions.UseLongPolling)}");
        if (useLongPolling)
        {
            services.AddHostedService<MaxLongPollingService>();
        }

        return services;
    }
}
