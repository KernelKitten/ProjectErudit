using Erudit.Domain.Accounting;
using Erudit.Domain.Calendar;
using Erudit.Domain.Lessons;
using Erudit.Domain.People;
using Erudit.Domain.School;
using Microsoft.EntityFrameworkCore;

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

    protected override void ConfigureConventions(ModelConfigurationBuilder configurationBuilder)
    {
        // Перечисления (enum) храним текстом: в базе видно "Held", а не непонятное 2.
        configurationBuilder.Properties<Enum>().HaveConversion<string>().HaveMaxLength(20);

        // Строки по умолчанию ограничиваем, чтобы в базу не попал «роман» вместо фамилии.
        configurationBuilder.Properties<string>().HaveMaxLength(200);
    }
}
