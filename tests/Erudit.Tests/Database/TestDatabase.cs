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

    /// <summary>Фабрика контекстов — для сервисов, которые, как страницы, создают контекст на каждую операцию.</summary>
    public IDbContextFactory<EruditDbContext> CreateFactory() => new Factory(this);

    private sealed class Factory(TestDatabase database) : IDbContextFactory<EruditDbContext>
    {
        public EruditDbContext CreateDbContext() => database.CreateContext();
    }

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

        // Запоминаем, что получилось сразу после заливки: другие тесты потом добавляют свои классы и людей,
        // и проверка «12 классов» не должна зависеть от того, в каком порядке запустились тесты.
        SeededClasses = await db.SchoolClasses.CountAsync();
        SeededTeachers = await db.People.CountAsync(p => p.Roles.Any(r => r.Role == Domain.People.Role.Teacher));
        SeededStudents = await db.People.CountAsync(p => p.Roles.Any(r => r.Role == Domain.People.Role.Student));
    }

    public int SeededClasses { get; private set; }
    public int SeededTeachers { get; private set; }
    public int SeededStudents { get; private set; }

    /// <summary>
    /// Отдельный учебный год для теста — «песочница»: классы и ученики теста не смешиваются с тестовыми данными
    /// и с другими тестами. У каждого теста свой год начала.
    /// </summary>
    public async Task<Domain.School.AcademicYear> CreateSandboxYearAsync(int startYear)
    {
        await using var db = CreateContext();
        var year = db.AcademicYears.Add(new Domain.School.AcademicYear
        {
            Name = $"{startYear}/{(startYear + 1) % 100:D2}",
            StartDate = new DateOnly(startYear, 9, 1),
            EndDate = new DateOnly(startYear + 1, 8, 31),
        }).Entity;
        await db.SaveChangesAsync();
        return year;
    }

    /// <summary>
    /// Готовит пустую тестовую базу с русской сортировкой ICU — как erudit_dev.
    /// Базы нет — создаём (EF Core создал бы её сам, но с сортировкой сервера по умолчанию).
    /// База есть — не удаляем её, а очищаем схему public: так подготовка работает,
    /// даже если к базе подключён pgAdmin (удалить базу с чужими подключениями нельзя).
    /// </summary>
    private static async Task RecreateDatabaseAsync()
    {
        var builder = new NpgsqlConnectionStringBuilder(ConnectionString);
        var databaseName = builder.Database!;
        builder.Database = "postgres"; // служебная база: в ней проверяем и создаём тестовую

        await using (var server = new NpgsqlConnection(builder.ConnectionString))
        {
            await server.OpenAsync();
            await using var exists = new NpgsqlCommand("SELECT 1 FROM pg_database WHERE datname = @name", server);
            exists.Parameters.AddWithValue("name", databaseName);
            if (await exists.ExecuteScalarAsync() is null)
            {
                await using var create = new NpgsqlCommand($"""
                    CREATE DATABASE "{databaseName}" ENCODING 'UTF8' TEMPLATE template0
                        LOCALE_PROVIDER icu ICU_LOCALE 'ru-RU' LOCALE 'C'
                    """, server);
                await create.ExecuteNonQueryAsync();
                return;
            }
        }

        await using var connection = new NpgsqlConnection(ConnectionString);
        await connection.OpenAsync();
        await using var reset = new NpgsqlCommand("DROP SCHEMA public CASCADE; CREATE SCHEMA public;", connection);
        await reset.ExecuteNonQueryAsync();
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
