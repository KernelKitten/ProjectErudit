using Erudit.Infrastructure.Lessons;
using Erudit.Tests.Database;
using Microsoft.EntityFrameworkCore;

namespace Erudit.Tests.Lessons;

/// <summary>Данные окна «Новое занятие» по тестовым данным (DevDataSeeder): только читаем.</summary>
[Collection(DatabaseCollection.Name)]
public class LessonFormServiceTests(TestDatabase database)
{
    private static readonly DateOnly Date = new(2026, 10, 21);

    private LessonFormService Service() => new(database.CreateFactory());

    [DatabaseFact]
    public async Task Справочники_классы_и_выбор_как_в_прошлый_раз()
    {
        await using var db = database.CreateContext();
        var sokolova = await db.People.SingleAsync(p => p.LastName == "Соколова");

        var data = await Service().LoadAsync(sokolova.Id, Date);

        Assert.Equal(10, data.Periods.Count);
        Assert.Contains(data.Classes, c => c.Name == "9а");
        Assert.Equal("1а", data.Classes[0].Name); // по параллелям
        // Последнее занятие Соколовой — разовое «Разбор пробника»: Математика, Каб.31.
        Assert.Equal("Математика", data.Subjects.Single(s => s.Id == data.LastSubjectId).Name);
        Assert.Equal("Каб.31", data.Rooms.Single(r => r.Id == data.LastRoomId).Name);
    }

    [DatabaseFact]
    public async Task Ученики_класса_по_алфавиту()
    {
        await using var db = database.CreateContext();
        var class9a = await db.SchoolClasses.SingleAsync(c => c.Grade == 9 && c.Letter == "а" && c.AcademicYear.StartDate.Year == 2026);

        var students = await Service().StudentsOfClassAsync(class9a.Id, Date);

        Assert.Equal(10, students.Count);
        Assert.All(students, s => Assert.Equal("9а", s.ClassName));
        Assert.Equal(students.OrderBy(s => s.Name, StringComparer.Create(new("ru-RU"), false)).Select(s => s.Id), students.Select(s => s.Id));
    }

    [DatabaseFact]
    public async Task Поиск_по_школе__без_регистра_и_ё()
    {
        await using var db = database.CreateContext();
        var anyStudent = await db.People.Where(p => p.Roles.Any(r => r.Role == Domain.People.Role.Student) && p.IsActive)
            .OrderBy(p => p.Id).FirstAsync();
        var query = anyStudent.LastName.ToUpperInvariant().Replace('Ё', 'Е');

        var found = await Service().SearchStudentsAsync(query, Date);

        Assert.Contains(found, s => s.Id == anyStudent.Id && s.ClassName is not null);
        Assert.Empty(await Service().SearchStudentsAsync("  ", Date));
    }
}
