using Erudit.Domain.Accounting;
using Erudit.Domain.Calendar;
using Erudit.Domain.Lessons;
using Erudit.Domain.People;
using Erudit.Domain.School;
using System.Text.Json;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.ChangeTracking;

namespace Erudit.Infrastructure.Persistence;

/// <summary>
/// Контекст базы данных: «окно» в PostgreSQL. Каждый DbSet — это таблица.
/// Как именно классы превращаются в таблицы, описано в папке Configurations.
/// </summary>
public class EruditDbContext(DbContextOptions<EruditDbContext> options) : DbContext(options)
{
    public DbSet<Person> People => Set<Person>();
    public DbSet<PersonRole> PersonRoles => Set<PersonRole>();
    public DbSet<ParentLink> ParentLinks => Set<ParentLink>();
    public DbSet<LoginRequest> LoginRequests => Set<LoginRequest>();

    public DbSet<AcademicYear> AcademicYears => Set<AcademicYear>();
    public DbSet<SchoolClass> SchoolClasses => Set<SchoolClass>();
    public DbSet<ClassMembership> ClassMemberships => Set<ClassMembership>();
    public DbSet<Subject> Subjects => Set<Subject>();
    public DbSet<Room> Rooms => Set<Room>();
    public DbSet<LessonPeriod> LessonPeriods => Set<LessonPeriod>();

    public DbSet<CalendarDay> CalendarDays => Set<CalendarDay>();
    public DbSet<Vacation> Vacations => Set<Vacation>();

    public DbSet<Series> Series => Set<Series>();
    public DbSet<SeriesEnrollment> SeriesEnrollments => Set<SeriesEnrollment>();
    public DbSet<Lesson> Lessons => Set<Lesson>();
    public DbSet<Enrollment> Enrollments => Set<Enrollment>();

    public DbSet<ClosedPeriod> ClosedPeriods => Set<ClosedPeriod>();
    public DbSet<AuditEntry> AuditEntries => Set<AuditEntry>();

    protected override void OnModelCreating(ModelBuilder modelBuilder)
    {
        // Подхватывает все классы IEntityTypeConfiguration<T> из этой сборки.
        modelBuilder.ApplyConfigurationsFromAssembly(typeof(EruditDbContext).Assembly);

        // По умолчанию EF удаляет связанные строки каскадом: удалили учителя — пропали его занятия.
        // Мы ничего не удаляем ради отчётов, поэтому запрещаем каскад везде, кроме ролей человека.
        foreach (var fk in modelBuilder.Model.GetEntityTypes().SelectMany(t => t.GetForeignKeys()))
        {
            if (fk.DeclaringEntityType.ClrType != typeof(PersonRole))
            {
                fk.DeleteBehavior = DeleteBehavior.Restrict;
            }
        }
    }

    // ---------------- Журнал изменений ----------------

    /// <summary>Кто сейчас работает с базой (Person.Id) — записывается в журнал. Пусто — сама система.</summary>
    public int? CurrentPersonId { get; set; }

    /// <summary>Вести журнал. Выключается для массовой загрузки тестовых данных.</summary>
    public bool AuditEnabled { get; set; } = true;

    /// <summary>
    /// Перед сохранением смотрим в ChangeTracker — «список изменений», который EF ведёт сам:
    /// что добавлено, изменено, удалено и какие поля поменялись. Для каждого изменения пишем строку журнала.
    /// У новых строк id появляется только после сохранения, поэтому сохраняем в два шага в одной транзакции.
    /// </summary>
    public override async Task<int> SaveChangesAsync(bool acceptAllChangesOnSuccess, CancellationToken ct = default)
    {
        if (!AuditEnabled)
        {
            return await base.SaveChangesAsync(acceptAllChangesOnSuccess, ct);
        }

        var changes = ChangeTracker.Entries()
            .Where(e => e.State is EntityState.Added or EntityState.Modified or EntityState.Deleted
                && e.Entity is not AuditEntry and not LoginRequest)
            .Select(e => (Entry: e, Action: e.State.ToString(), Fields: DescribeChanges(e)))
            .ToList();
        if (changes.Count == 0)
        {
            return await base.SaveChangesAsync(acceptAllChangesOnSuccess, ct);
        }

        // Если транзакция уже открыта снаружи — используем её, иначе открываем свою.
        var ownTransaction = Database.CurrentTransaction is null ? await Database.BeginTransactionAsync(ct) : null;
        try
        {
            var result = await base.SaveChangesAsync(acceptAllChangesOnSuccess, ct);
            var now = DateTime.UtcNow;
            foreach (var (entry, action, fields) in changes)
            {
                AuditEntries.Add(new AuditEntry
                {
                    At = now,
                    PersonId = CurrentPersonId,
                    EntityType = entry.Metadata.ClrType.Name,
                    EntityId = string.Join(",", entry.Metadata.FindPrimaryKey()!.Properties
                        .Select(p => entry.Property(p.Name).CurrentValue?.ToString())),
                    Action = action,
                    Changes = fields.Count == 0 ? null : JsonSerializer.Serialize(fields),
                });
            }
            await base.SaveChangesAsync(acceptAllChangesOnSuccess, ct);

            if (ownTransaction is not null)
            {
                await ownTransaction.CommitAsync(ct);
            }
            return result;
        }
        finally
        {
            if (ownTransaction is not null)
            {
                await ownTransaction.DisposeAsync(); // без Commit — откат: в базе не останется половины изменений
            }
        }
    }

    /// <summary>Добавлено — новые значения полей; изменено — {"поле": [было, стало]}; удалено — прежние значения.</summary>
    private static Dictionary<string, object?> DescribeChanges(EntityEntry entry) => entry.State switch
    {
        EntityState.Added => entry.Properties
            .Where(p => !p.Metadata.IsPrimaryKey())
            .ToDictionary(p => p.Metadata.Name, p => p.CurrentValue),
        EntityState.Modified => entry.Properties
            .Where(p => p.IsModified && !Equals(p.OriginalValue, p.CurrentValue))
            .ToDictionary(p => p.Metadata.Name, p => (object?)new[] { p.OriginalValue, p.CurrentValue }),
        _ => entry.Properties.ToDictionary(p => p.Metadata.Name, p => p.OriginalValue),
    };

    protected override void ConfigureConventions(ModelConfigurationBuilder configurationBuilder)
    {
        // Перечисления (enum) храним текстом: в базе видно "Held", а не непонятное 2.
        configurationBuilder.Properties<Enum>().HaveConversion<string>().HaveMaxLength(20);

        // Строки по умолчанию ограничиваем, чтобы в базу не попал «роман» вместо фамилии.
        configurationBuilder.Properties<string>().HaveMaxLength(200);
    }
}
