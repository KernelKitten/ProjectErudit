using Erudit.Infrastructure.Persistence;
using Erudit.Infrastructure.Persistence.Seed;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Configuration;
using Npgsql;

namespace Erudit.Tests.Database;

/// <summary>
/// Отдельная тестовая база (erudit_test): перед тестами пересоздаётся с нуля,
/// применяются все миграции и тестовые данные. Рабочую базу разработки (erudit_dev) не трогает.
///
/// Строка подключения берётся из User Secrets тестового проекта или переменной окружения
/// ConnectionStrings__EruditTest. Если её нет — тесты с базой пропускаются.
/// </summary>
public sealed class TestDatabase : IAsyncLifetime
{
    /// <summary>«Сейчас» для тестовых данных — фиксированное, чтобы результаты не зависели от дня запуска.</summary>
    public static readonly DateTime Now = new(2026, 10, 17, 12, 0, 0, DateTimeKind.Utc);

    public static string? ConnectionString { get; } = new ConfigurationBuilder()
        .AddUserSecrets<TestDatabase>(optional: true)
        .AddEnvironmentVariables()
        .Build()
        .GetConnectionString("EruditTest");

    public EruditDbContext CreateContext() => new(new DbContextOptionsBuilder<EruditDbContext>()
        .UseNpgsql(ConnectionString)
        .UseSnakeCaseNamingConvention()
        .Options);

    public async Task InitializeAsync()
    {
        if (ConnectionString is null)
        {
            return;
        }
        await RecreateDatabaseAsync();
        await using var db = CreateContext();
        await db.Database.MigrateAsync();
        await DevDataSeeder.SeedAsync(db, Now);
    }

    /// <summary>
    /// Пересоздаёт тестовую базу с русской сортировкой ICU — как erudit_dev.
    /// (EF Core создал бы базу сам, но с сортировкой сервера по умолчанию.)
    /// </summary>
    private static async Task RecreateDatabaseAsync()
    {
        var builder = new NpgsqlConnectionStringBuilder(ConnectionString);
        var databaseName = builder.Database!;
        builder.Database = "postgres"; // служебная база: к удаляемой подключаться нельзя

        await using var connection = new NpgsqlConnection(builder.ConnectionString);
        await connection.OpenAsync();
        await using var command = connection.CreateCommand();
        command.CommandText = $"""
            DROP DATABASE IF EXISTS "{databaseName}" WITH (FORCE);
            CREATE DATABASE "{databaseName}" ENCODING 'UTF8' TEMPLATE template0
                LOCALE_PROVIDER icu ICU_LOCALE 'ru-RU' LOCALE 'C';
            """;
        await command.ExecuteNonQueryAsync();
    }

    public Task DisposeAsync() => Task.CompletedTask;
}

/// <summary>Все тесты с базой — в одной «коллекции»: база создаётся один раз, тесты идут по очереди.</summary>
[CollectionDefinition(Name)]
public sealed class DatabaseCollection : ICollectionFixture<TestDatabase>
{
    public const string Name = "Database";
}

/// <summary>Как [Fact], но пропускается, если строка подключения к тестовой базе не настроена.</summary>
public sealed class DatabaseFactAttribute : FactAttribute
{
    public DatabaseFactAttribute()
    {
        if (TestDatabase.ConnectionString is null)
        {
            Skip = "Не задана строка подключения ConnectionStrings:EruditTest (User Secrets тестового проекта).";
        }
    }
}
