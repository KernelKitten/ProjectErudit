using Erudit.Domain.People;
using Erudit.Infrastructure.Auth;
using Erudit.Tests.Database;
using Microsoft.EntityFrameworkCore;

namespace Erudit.Tests.Auth;

/// <summary>Запасной вход по телефону и паролю. Люди — со своими номерами +7098…, учебный год не нужен.</summary>
[Collection(DatabaseCollection.Name)]
public class PasswordServiceTests(TestDatabase database)
{
    private sealed class Clock(DateTime utc) : TimeProvider
    {
        public DateTime Utc { get; set; } = utc;
        public override DateTimeOffset GetUtcNow() => Utc;
    }

    private async Task<(Person Teacher, Person Student, Person Admin)> PeopleAsync(string suffix)
    {
        await using var db = database.CreateContext();
        Person New(string last, string phone, Role role, long? max = null) => new()
        {
            LastName = last, FirstName = "Тест", Phone = phone, MaxUserId = max, Roles = [new PersonRole { Role = role }],
        };
        var teacher = New($"Учитель{suffix}", $"+709800{suffix}1", Role.Teacher, max: long.Parse($"9800{suffix}1"));
        var student = New($"Родитель{suffix}", $"+709800{suffix}2", Role.Parent); // родителю (как и ученику) пароль не положен; ученик без класса сломал бы проверку тестовых данных
        var admin = New($"Админ{suffix}", $"+709800{suffix}3", Role.Admin);
        db.AddRange(teacher, student, admin);
        await db.SaveChangesAsync();
        return (teacher, student, admin);
    }

    [DatabaseFact]
    public async Task Временный_пароль__вход_со_сменой__свой_пароль__журнал_без_хэша()
    {
        var (teacher, _, admin) = await PeopleAsync("0001");
        var clock = new Clock(new DateTime(2026, 11, 13, 9, 0, 0, DateTimeKind.Utc));
        await using var db = database.CreateContext();
        var service = new PasswordService(db, clock);

        var temp = await service.IssueTemporaryAsync(teacher.Id, admin.Id);
        Assert.Matches("^[a-z2-9]{4}-[a-z2-9]{4}-[a-z2-9]{4}$", temp);

        // Телефон в любом виде (номер учителя +70980000011); временный пароль — вход с требованием сменить.
        Assert.Equal(PasswordLoginStatus.Invalid, (await service.LoginAsync("8 (098) 000-00-99", temp)).Status); // чужой номер
        var ok = await service.LoginAsync("8 (098) 000-00-11", temp);
        Assert.Equal((PasswordLoginStatus.Ok, teacher.Id, true), (ok.Status, ok.PersonId, ok.MustChange));

        Assert.Contains("не короче", (await Assert.ThrowsAsync<InvalidOperationException>(() => service.SetOwnAsync(teacher.Id, "short"))).Message);
        await service.SetOwnAsync(teacher.Id, "новый пароль 2026");
        Assert.Equal(PasswordLoginStatus.Invalid, (await service.LoginAsync(teacher.Phone, temp)).Status);
        var again = await service.LoginAsync(teacher.Phone, "новый пароль 2026");
        Assert.Equal((PasswordLoginStatus.Ok, false), (again.Status, again.MustChange));

        var stored = await db.PersonPasswords.SingleAsync(p => p.PersonId == teacher.Id);
        Assert.DoesNotContain("новый", stored.Hash); // в базе только хэш
        var audit = await db.AuditEntries.Where(e => e.EntityType == "Person" && e.EntityId == teacher.Id.ToString())
            .OrderBy(e => e.Id).Select(e => new { e.Action, e.PersonId }).ToListAsync();
        Assert.Equal(["PasswordTempIssued", "PasswordLogin", "PasswordSet", "PasswordLoginFailed", "PasswordLogin"],
            audit.Where(a => a.Action.StartsWith("Password")).Select(a => a.Action));
        Assert.Equal(admin.Id, audit.First(a => a.Action == "PasswordTempIssued").PersonId);
        Assert.DoesNotContain(await db.AuditEntries.Where(e => e.Changes != null).Select(e => e.Changes!).ToListAsync(), c => c.Contains(stored.Hash));
        Assert.False(await db.AuditEntries.AnyAsync(e => e.EntityType == "PersonPassword"));

        // У учителя есть MAX — два сообщения: выдан временный и изменён.
        Assert.Equal(2, await db.Notifications.CountAsync(n => n.RecipientId == teacher.Id && n.Text.StartsWith("🔑")));
    }

    [DatabaseFact]
    public async Task Пять_неверных__блокировка_на_15_минут__родителю_пароль_нельзя()
    {
        var (teacher, student, admin) = await PeopleAsync("0002");
        var clock = new Clock(new DateTime(2026, 11, 13, 9, 0, 0, DateTimeKind.Utc));
        await using var db = database.CreateContext();
        var service = new PasswordService(db, clock);
        await service.SetOwnAsync(teacher.Id, "правильный пароль");

        for (var i = 1; i < PersonPassword.MaxFailures; i++)
        {
            Assert.Equal(PasswordLoginStatus.Invalid, (await service.LoginAsync(teacher.Phone, "неверный")).Status);
        }
        var locked = await service.LoginAsync(teacher.Phone, "неверный");
        Assert.Equal(PasswordLoginStatus.Locked, locked.Status);
        Assert.Equal(clock.Utc.AddMinutes(15), locked.LockedUntil);
        // Пока закрыто — даже правильный пароль не пускает.
        Assert.Equal(PasswordLoginStatus.Locked, (await service.LoginAsync(teacher.Phone, "правильный пароль")).Status);

        clock.Utc = clock.Utc.AddMinutes(16);
        Assert.Equal(PasswordLoginStatus.Ok, (await service.LoginAsync(teacher.Phone, "правильный пароль")).Status);

        // Родителю пароль не положен — ни задать, ни выдать.
        await Assert.ThrowsAsync<InvalidOperationException>(() => service.SetOwnAsync(student.Id, "пароль родителя"));
        await Assert.ThrowsAsync<InvalidOperationException>(() => service.IssueTemporaryAsync(student.Id, admin.Id));
        Assert.Equal(PasswordLoginStatus.Invalid, (await service.LoginAsync(student.Phone, "пароль родителя")).Status);
        Assert.Equal(PasswordLoginStatus.Invalid, (await service.LoginAsync("неномер", "что угодно")).Status);
        Assert.Equal((false, false), await service.StateAsync(student.Id));
        Assert.Equal((true, true), await service.StateAsync(teacher.Id));
    }
}
