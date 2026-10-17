using System.Text.Json;
using Erudit.Domain.School;
using Microsoft.EntityFrameworkCore;

namespace Erudit.Tests.Database;

/// <summary>Журнал изменений: каждое сохранение записывает, кто и что поменял.</summary>
[Collection(DatabaseCollection.Name)]
public class AuditTests(TestDatabase database)
{
    [DatabaseFact]
    public async Task Добавление_и_изменение_попадают_в_журнал_с_автором()
    {
        int roomId;
        await using (var db = database.CreateContext())
        {
            db.CurrentPersonId = 3;
            var room = db.Rooms.Add(new Room { Name = "Каб.Аудит", Floor = 2 }).Entity;
            await db.SaveChangesAsync();
            roomId = room.Id;

            room.Floor = 3;
            await db.SaveChangesAsync();
        }

        await using var check = database.CreateContext();
        var entries = await check.AuditEntries
            .Where(e => e.EntityType == nameof(Room) && e.EntityId == roomId.ToString())
            .OrderBy(e => e.Id)
            .ToListAsync();

        Assert.Equal(["Added", "Modified"], entries.Select(e => e.Action));
        Assert.All(entries, e => Assert.Equal(3, e.PersonId));

        // Для изменения записано «было → стало» и только изменённое поле.
        var changes = JsonDocument.Parse(entries[1].Changes!).RootElement;
        Assert.Equal(["Floor"], changes.EnumerateObject().Select(p => p.Name));
        Assert.Equal([2, 3], changes.GetProperty("Floor").EnumerateArray().Select(v => v.GetInt32()));
    }

    [DatabaseFact]
    public async Task Ошибка_сохранения__ни_данных_ни_записей_журнала()
    {
        await using var db = database.CreateContext();
        var year = await db.AcademicYears.FirstAsync();
        var auditBefore = await db.AuditEntries.CountAsync();

        db.SchoolClasses.Add(new SchoolClass { AcademicYear = year, Grade = 12, Letter = "з" }); // нарушает CHECK

        await Assert.ThrowsAsync<DbUpdateException>(() => db.SaveChangesAsync());
        await using var check = database.CreateContext();
        Assert.Equal(auditBefore, await check.AuditEntries.CountAsync());
    }
}
