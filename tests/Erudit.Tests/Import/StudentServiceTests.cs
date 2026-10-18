using Erudit.Domain.People;
using Erudit.Domain.School;
using Erudit.Infrastructure.People;
using Erudit.Tests.Database;
using Microsoft.EntityFrameworkCore;

namespace Erudit.Tests.Import;

[Collection(DatabaseCollection.Name)]
public class StudentServiceTests(TestDatabase database)
{
    private static readonly DateOnly Today = new(2026, 10, 17);

    /// <summary>Новый пустой класс в тестовом учебном году (у каждого теста — свой, с уникальной буквой).</summary>
    private async Task<int> NewClassAsync(int grade, string letter)
    {
        await using var db = database.CreateContext();
        var year = await db.AcademicYears.FirstAsync();
        var c = db.SchoolClasses.Add(new SchoolClass { AcademicYear = year, Grade = grade, Letter = letter }).Entity;
        await db.SaveChangesAsync();
        return c.Id;
    }

    [DatabaseFact]
    public async Task Загрузка_класса__родители_братьев_и_сестёр_общие()
    {
        var classId = await NewClassAsync(2, "ю");
        using var file = TestExcel.Create(
        [
            ["ФИО ученика", "Телефон", "Мама", "Телефон мамы", "Папа", "Телефон папы"],
            ["Лисицына Алиса Петровна", "", "Лисицына Ольга Ивановна", "79005551001", "Лисицын Пётр Ильич", "79005551002"],
            ["Лисицын Марк Петрович", "", "Лисицына Ольга Ивановна", "8 900 555-10-01", "", ""], // та же мама
            ["Совин Глеб", "79005551003", "Совина Анна", "", "", ""],                            // мама без телефона
        ]);

        await using (var db = database.CreateContext())
        {
            var service = new StudentService(db);
            var preview = await service.PreviewAsync(classId, file);

            Assert.False(preview.HasErrors, string.Join("; ", preview.Rows.SelectMany(r => r.Errors)));
            Assert.Contains(preview.Rows[2].Warnings, w => w.Contains("без телефона"));
            // «Телефон мамы» не перепутан с «Телефон» ученика:
            Assert.Null(preview.Rows[0].Phone);
            Assert.Equal("+79005551001", preview.Rows[0].Parents[0].Phone);

            Assert.Equal(3, await service.ApplyAsync(classId, preview, Today));
        }

        await using var check = database.CreateContext();
        var mom = await check.People.SingleAsync(p => p.Phone == "+79005551001");
        var children = await check.ParentLinks.Where(l => l.ParentId == mom.Id).ToListAsync();
        Assert.Equal(2, children.Count);                 // одна мама — два ребёнка
        Assert.All(children, l => Assert.True(l.NotifyAll)); // 2-й класс — все уведомления родителям
        Assert.Equal(3, await check.ClassMemberships.CountAsync(m => m.SchoolClassId == classId && m.ToDate == null));
        Assert.False(await check.People.AnyAsync(p => p.LastName == "Совина")); // родитель без телефона не создан
    }

    [DatabaseFact]
    public async Task Загрузка__ошибки_и_запрет_для_непустого_класса()
    {
        var classId = await NewClassAsync(6, "ю");
        string studentPhone;
        await using (var db = database.CreateContext())
        {
            studentPhone = (await db.People.FirstAsync(p => p.Phone != null && p.Roles.Any(r => r.Role == Role.Student))).Phone!;
        }
        using var bad = TestExcel.Create(
        [
            ["ФИО", "Телефон ученика", "Родитель 1", "Телефон родителя 1"],
            ["Ежов Иван", "79005552001", "Ежова Мария", studentPhone],          // телефон родителя — у ученика
            ["Ежова Анна", "79005552001", "", ""],                               // телефон ученика повторяется
            ["Котов Лев", "", "Котова Нина", "79005552002"],
            ["Котова Ева", "", "Мышкина Нина", "79005552002"],                    // один телефон — разные родители
        ]);

        await using (var db = database.CreateContext())
        {
            var preview = await new StudentService(db).PreviewAsync(classId, bad);
            Assert.True(preview.HasErrors);
            Assert.Contains(preview.Rows[0].Errors, e => e.Contains("у ученика"));
            Assert.Contains(preview.Rows[1].Errors, e => e.Contains("повторяется"));
            Assert.Contains(preview.Rows[3].Errors, e => e.Contains("у разных людей"));
        }

        // В классе уже есть ученик — загрузка из Excel запрещена.
        await using (var db = database.CreateContext())
        {
            Assert.Null(await new StudentService(db).SaveStudentAsync(classId, null, new StudentInput("Ранний Ученик", null, []), Today));
        }
        using var ok = TestExcel.Create([["ФИО"], ["Поздний Ученик"]]);
        await using (var db = database.CreateContext())
        {
            var service = new StudentService(db);
            var preview = await service.PreviewAsync(classId, ok);
            await Assert.ThrowsAsync<InvalidOperationException>(() => service.ApplyAsync(classId, preview, Today));
        }
    }

    [DatabaseFact]
    public async Task Карточка__добавить_с_родителем__потом_отвязать_родителя()
    {
        var classId = await NewClassAsync(8, "ю");
        await using (var db = database.CreateContext())
        {
            var error = await new StudentService(db).SaveStudentAsync(classId, null, new StudentInput("Зайкин Олег",
                "79005553001", [new ParentInput(null, "Зайкина Вера", "79005553002", NotifyAll: false)]), Today);
            Assert.Null(error);
        }

        await using (var db = database.CreateContext())
        {
            var student = await db.People.SingleAsync(p => p.Phone == "+79005553001");
            Assert.Single(await db.ParentLinks.Where(l => l.StudentId == student.Id).ToListAsync());

            // Убрали родителя из карточки — связь удалена, сам родитель остался в базе.
            var error = await new StudentService(db).SaveStudentAsync(classId, student.Id, new StudentInput("Зайкин Олег Ильич", "79005553001", []), Today);
            Assert.Null(error);
        }

        await using var check = database.CreateContext();
        var saved = await check.People.SingleAsync(p => p.Phone == "+79005553001");
        Assert.Equal("Ильич", saved.MiddleName);
        Assert.False(await check.ParentLinks.AnyAsync(l => l.StudentId == saved.Id));
        Assert.True(await check.People.AnyAsync(p => p.Phone == "+79005553002"));
    }

    [DatabaseFact]
    public async Task Перевод_и_выбытие()
    {
        var fromId = await NewClassAsync(9, "ю");
        var toId = await NewClassAsync(9, "я");
        int studentId;
        await using (var db = database.CreateContext())
        {
            Assert.Null(await new StudentService(db).SaveStudentAsync(fromId, null, new StudentInput("Переводов Пётр", null, []), Today));
            studentId = (await db.People.SingleAsync(p => p.LastName == "Переводов")).Id;
        }

        await using (var db = database.CreateContext())
        {
            var service = new StudentService(db);
            Assert.NotNull(await service.TransferAsync(studentId, fromId, toId, Today)); // в день прихода — нельзя
            Assert.Null(await service.TransferAsync(studentId, fromId, toId, Today.AddDays(10)));
        }

        await using (var db = database.CreateContext())
        {
            var memberships = await db.ClassMemberships.Where(m => m.StudentId == studentId).OrderBy(m => m.FromDate).ToListAsync();
            Assert.Equal(Today.AddDays(9), memberships[0].ToDate);
            Assert.Equal((toId, Today.AddDays(10), (DateOnly?)null), (memberships[1].SchoolClassId, memberships[1].FromDate, memberships[1].ToDate));

            Assert.Null(await new StudentService(db).LeaveAsync(studentId, toId, Today.AddDays(20)));
        }

        await using var check = database.CreateContext();
        Assert.False((await check.People.SingleAsync(p => p.Id == studentId)).IsActive);
        Assert.False(await check.ClassMemberships.AnyAsync(m => m.StudentId == studentId && m.ToDate == null));
    }
}
