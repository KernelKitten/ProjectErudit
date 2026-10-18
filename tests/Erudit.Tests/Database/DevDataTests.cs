using Erudit.Domain;
using Erudit.Domain.Lessons;
using Erudit.Domain.People;
using Erudit.Domain.School;
using Erudit.Infrastructure.Persistence.Seed;
using Microsoft.EntityFrameworkCore;

namespace Erudit.Tests.Database;

/// <summary>
/// Интеграционные тесты: настоящий PostgreSQL, настоящие миграции и тестовые данные.
/// Проверяют то, чего не видно в модульных тестах: ограничения базы и связность данных.
/// </summary>
[Collection(DatabaseCollection.Name)]
public class DevDataTests(TestDatabase database)
{
    [DatabaseFact]
    public async Task Тестовые_данные_созданы()
    {
        await using var db = database.CreateContext();

        // Количества сразу после заливки (другие тесты потом добавляют свои данные).
        Assert.Equal(12, database.SeededClasses);
        Assert.Equal(8, database.SeededTeachers);
        Assert.Equal(120, database.SeededStudents);
        Assert.Equal(10, await db.LessonPeriods.CountAsync());
        Assert.True(await db.Lessons.CountAsync() > 50);
    }

    [DatabaseFact]
    public async Task Повторный_запуск_ничего_не_добавляет()
    {
        await using var db = database.CreateContext();

        Assert.False(await DevDataSeeder.SeedAsync(db, TestDatabase.Now));
    }

    [DatabaseFact]
    public async Task У_каждого_ученика_ровно_один_текущий_класс()
    {
        await using var db = database.CreateContext();

        var studentsWithoutSingleClass = await db.People
            .Where(p => p.Roles.Any(r => r.Role == Role.Student))
            .Where(p => db.ClassMemberships.Count(m => m.StudentId == p.Id && m.ToDate == null) != 1)
            .CountAsync();

        Assert.Equal(0, studentsWithoutSingleClass);
    }

    [DatabaseFact]
    public async Task Переведённый_ученик_сейчас_в_7б_а_в_сентябре_был_в_7а()
    {
        await using var db = database.CreateContext();

        var transferred = await db.ClassMemberships
            .Where(m => m.ToDate != null)
            .Select(m => m.StudentId)
            .SingleAsync();
        var memberships = await db.ClassMemberships
            .Include(m => m.SchoolClass)
            .Where(m => m.StudentId == transferred)
            .ToListAsync();

        Assert.Equal("7а", memberships.Single(m => m.IsActiveOn(new DateOnly(2026, 9, 15))).SchoolClass.Name);
        Assert.Equal("7б", memberships.Single(m => m.IsActiveOn(new DateOnly(2026, 10, 15))).SchoolClass.Name);
    }

    [DatabaseFact]
    public async Task Занятия_серий_не_попадают_на_праздники()
    {
        await using var db = database.CreateContext();
        var holidays = await db.CalendarDays.Where(d => !d.IsWorkingDay).Select(d => d.Date).ToListAsync();

        var seriesLessonDates = (await db.Lessons.Where(l => l.SeriesId != null).Select(l => l.StartsAt).ToListAsync())
            .Select(utc => DateOnly.FromDateTime(SchoolTime.ToSchool(utc)));

        Assert.DoesNotContain(seriesLessonDates, holidays.Contains);
    }

    [DatabaseFact]
    public async Task Прошедшие_занятия_отмечены_будущие_запланированы()
    {
        await using var db = database.CreateContext();

        Assert.False(await db.Lessons.AnyAsync(l => l.StartsAt > TestDatabase.Now && l.Status == LessonStatus.Held));
        Assert.False(await db.Lessons.AnyAsync(l =>
            l.StartsAt.AddMinutes(l.DurationMinutes) < TestDatabase.Now && l.Status == LessonStatus.Planned));
    }

    [DatabaseFact]
    public async Task Нет_двух_занятий_в_одном_кабинете_одновременно()
    {
        await using var db = database.CreateContext();
        var lessons = await db.Lessons.Where(l => l.Status != LessonStatus.Cancelled).ToListAsync();

        var conflicts = lessons
            .SelectMany(a => lessons.Where(b => b.Id > a.Id && b.RoomId == a.RoomId
                && a.StartsAt < b.StartsAt.AddMinutes(b.DurationMinutes)
                && b.StartsAt < a.StartsAt.AddMinutes(a.DurationMinutes)))
            .ToList();

        Assert.Empty(conflicts);
    }

    [DatabaseFact]
    public async Task Фамилии_сортируются_по_русски()
    {
        await using var db = database.CreateContext();

        // Сначала Distinct, потом OrderBy: в обратном порядке SQL (SELECT DISTINCT) теряет сортировку.
        var lastNames = await db.People.Select(p => p.LastName).Distinct().OrderBy(n => n).ToListAsync();

        // «Ёлкин» должен стоять среди «Е», а не в начале списка, как при сортировке по кодам символов.
        var yolkin = lastNames.FindIndex(n => n.StartsWith('Ё'));
        Assert.True(yolkin > 0, "Фамилия на «Ё» не должна быть первой");
        Assert.StartsWith("Е", lastNames[yolkin - 1], StringComparison.Ordinal);
    }

    [DatabaseFact]
    public async Task База_не_принимает_12_класс()
    {
        await using var db = database.CreateContext();
        var year = await db.AcademicYears.FirstAsync();

        db.SchoolClasses.Add(new SchoolClass { AcademicYear = year, Grade = 12, Letter = "я" });

        // Ограничение CHECK в PostgreSQL срабатывает, даже если код забыл проверку.
        await Assert.ThrowsAsync<DbUpdateException>(() => db.SaveChangesAsync());
    }

    [DatabaseFact]
    public async Task База_не_принимает_повторный_телефон()
    {
        await using var db = database.CreateContext();
        var existingPhone = await db.People.Where(p => p.Phone != null).Select(p => p.Phone).FirstAsync();

        db.People.Add(new Person { LastName = "Двойников", FirstName = "Пётр", Phone = existingPhone });

        await Assert.ThrowsAsync<DbUpdateException>(() => db.SaveChangesAsync());
    }
}
