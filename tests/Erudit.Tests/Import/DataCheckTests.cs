using Erudit.Domain.Accounting;
using Erudit.Domain.People;
using Erudit.Domain.School;
using Erudit.Infrastructure.People;
using Erudit.Tests.Database;
using Microsoft.EntityFrameworkCore;

namespace Erudit.Tests.Import;

[Collection(DatabaseCollection.Name)]
public class DataCheckTests(TestDatabase database)
{
    [DatabaseFact]
    public async Task Отчёт_находит_проблемы()
    {
        int yearId;
        string knownPhone;
        await using (var db = database.CreateContext())
        {
            var year = await db.AcademicYears.FirstAsync();
            yearId = year.Id;
            knownPhone = (await db.People.FirstAsync(p => p.Phone != null)).Phone!;

            // Класс без руководителя, в нём ученик без телефона и без родителей.
            var schoolClass = db.SchoolClasses.Add(new SchoolClass { AcademicYear = year, Grade = 3, Letter = "щ" }).Entity;
            var lonely = new Person { LastName = "Безтелефонов", FirstName = "Тихон", Roles = [new PersonRole { Role = Role.Student }] };
            db.ClassMemberships.Add(new ClassMembership { Student = lonely, SchoolClass = schoolClass, FromDate = year.StartDate });
            // Учитель без телефона.
            db.People.Add(new Person { LastName = "Беззвонкова", FirstName = "Ада", Roles = [new PersonRole { Role = Role.Teacher }] });
            // Попытки входа с неизвестным номером (дважды) и с номером, который потом добавили.
            db.AuditEntries.AddRange(
                new AuditEntry { At = DateTime.UtcNow.AddHours(-2), EntityType = "Registration", EntityId = "1", Action = "PhoneNotFound", Changes = """{"phone":"+79005559999"}""" },
                new AuditEntry { At = DateTime.UtcNow.AddHours(-1), EntityType = "Registration", EntityId = "1", Action = "PhoneNotFound", Changes = """{"phone":"+79005559999"}""" },
                new AuditEntry { At = DateTime.UtcNow, EntityType = "Registration", EntityId = "2", Action = "PhoneNotFound", Changes = $$"""{"phone":"{{knownPhone}}"}""" });
            await db.SaveChangesAsync();
        }

        await using var check = database.CreateContext();
        var report = await new DataCheckService(check).BuildAsync(yearId);

        Assert.Contains("3щ", report.ClassesWithoutTeacher);
        Assert.Contains(report.UnreachableStudents, s => s.Name == "Безтелефонов Тихон" && s.ClassName == "3щ");
        Assert.Contains("Беззвонкова Ада", report.TeachersWithoutPhone);

        var unknown = report.UnknownPhoneAttempts.Single(a => a.Phone == "+79005559999");
        Assert.Equal(2, unknown.Count);      // две попытки — одна строка
        Assert.False(unknown.NowKnown);
        Assert.True(report.UnknownPhoneAttempts.Single(a => a.Phone == knownPhone).NowKnown);

        // Сводка: 7а из тестовых данных — 9 учеников (один переведён в 7б), у всех есть телефон.
        var row7a = report.Classes.Single(c => c.ClassName == "7а");
        Assert.Equal((9, 9), (row7a.Students, row7a.StudentsWithPhone));
    }
}
