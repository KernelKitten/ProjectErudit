using Erudit.Domain;
using Erudit.Domain.Lessons;
using Erudit.Domain.People;
using Erudit.Domain.School;
using Erudit.Infrastructure.Accounting;
using Erudit.Tests.Database;
using Microsoft.EntityFrameworkCore;

namespace Erudit.Tests.Accounting;

/// <summary>Закрытие и открытие месяца. Свой учебный год у каждого теста (2095–2096).</summary>
[Collection(DatabaseCollection.Name)]
public class PeriodServiceTests(TestDatabase database)
{
    private sealed record World(AcademicYear Year, int TeacherId, int SubjectId, int RoomId);

    private async Task<World> CreateWorldAsync(int year)
    {
        var academicYear = await database.CreateSandboxYearAsync(year);
        await using var db = database.CreateContext();
        var teacher = new Person { LastName = $"Учитель{year}", FirstName = "Тест", Roles = [new PersonRole { Role = Role.Teacher }] };
        var subject = new Subject { Name = $"Предмет {year}" };
        var room = new Room { Name = $"Каб.{year}" };
        db.AddRange(teacher, subject, room);
        await db.SaveChangesAsync();
        return new World(academicYear, teacher.Id, subject.Id, room.Id);
    }

    private async Task AddLessonAsync(World w, DateOnly date, TimeOnly start, LessonStatus status = LessonStatus.Planned)
    {
        await using var db = database.CreateContext();
        db.Add(new Lesson
        {
            TeacherId = w.TeacherId, SubjectId = w.SubjectId, RoomId = w.RoomId, StartsAt = SchoolTime.ToUtc(date, start),
            DurationMinutes = 40, Capacity = 5, Status = status,
        });
        await db.SaveChangesAsync();
    }

    private PeriodService Service(Erudit.Infrastructure.Persistence.EruditDbContext db, DateOnly date, TimeOnly at) =>
        new(db, new FixedTime(SchoolTime.ToUtc(date, at)));

    [DatabaseFact]
    public async Task Месяцы_года__счётчики__неотмеченные_и_идущий_месяц()
    {
        var w = await CreateWorldAsync(2095);
        var september = new DateOnly(2095, 9, 1);
        var october = new DateOnly(2095, 10, 1);
        await AddLessonAsync(w, september.AddDays(10), new TimeOnly(14, 15), LessonStatus.Held);
        await AddLessonAsync(w, september.AddDays(11), new TimeOnly(14, 15), LessonStatus.NotHeld);
        await AddLessonAsync(w, september.AddDays(12), new TimeOnly(14, 15), LessonStatus.Cancelled);
        await AddLessonAsync(w, september.AddDays(13), new TimeOnly(14, 15)); // не отмечено
        await AddLessonAsync(w, october.AddDays(2), new TimeOnly(14, 15)); // прошло, не отмечено
        await AddLessonAsync(w, october.AddDays(20), new TimeOnly(14, 15)); // ещё впереди

        await using var db = database.CreateContext();
        var service = Service(db, october.AddDays(9), new TimeOnly(12, 0));
        var months = await service.MonthsAsync(w.Year);

        Assert.Equal(12, months.Count);
        Assert.Equal(september, months[0].Month);
        Assert.Equal(new DateOnly(2096, 8, 1), months[^1].Month);
        var sep = months[0];
        Assert.True(sep.Ended);
        Assert.Equal((1, 1, 1, 1, 0), (sep.Held, sep.NotHeld, sep.Cancelled, sep.Unmarked, sep.Upcoming));
        Assert.Null(sep.Closed);
        var oct = months[1];
        Assert.False(oct.Ended);
        Assert.Equal((1, 1), (oct.Unmarked, oct.Upcoming));

        var unmarked = Assert.Single(await service.UnmarkedAsync(september));
        Assert.Equal($"Учитель2095 Т.", unmarked.Teacher);
        Assert.Equal($"Предмет 2095", unmarked.Subject);
    }

    [DatabaseFact]
    public async Task Закрыть__только_прошедший__дважды_нельзя__открыть__всё_в_журнале()
    {
        var w = await CreateWorldAsync(2096);
        var september = new DateOnly(2096, 9, 1);
        await using var db = database.CreateContext();
        db.CurrentPersonId = w.TeacherId;

        // 30 сентября ещё идёт.
        var tooEarly = Service(db, new DateOnly(2096, 9, 30), new TimeOnly(23, 0));
        var error = await Assert.ThrowsAsync<InvalidOperationException>(() => tooEarly.CloseAsync(september, w.TeacherId));
        Assert.Contains("ещё не закончился", error.Message);

        var service = Service(db, new DateOnly(2096, 10, 1), new TimeOnly(0, 5));
        await service.CloseAsync(september.AddDays(14), w.TeacherId); // любое число месяца — закрывается весь месяц
        var closed = (await service.MonthsAsync(w.Year))[0].Closed;
        Assert.Equal("Учитель2096 Т.", closed?.By);
        Assert.Contains("уже закрыт", (await Assert.ThrowsAsync<InvalidOperationException>(() => service.CloseAsync(september, w.TeacherId))).Message);

        await service.OpenAsync(september);
        Assert.Null((await service.MonthsAsync(w.Year))[0].Closed);
        Assert.Contains("не закрыт", (await Assert.ThrowsAsync<InvalidOperationException>(() => service.OpenAsync(september))).Message);

        var audit = await db.AuditEntries.Where(e => e.EntityType == "ClosedPeriod" && e.PersonId == w.TeacherId)
            .OrderBy(e => e.Id).Select(e => e.Action).ToListAsync();
        Assert.Equal(["Added", "Deleted"], audit);
    }

    private sealed class FixedTime(DateTime utc) : TimeProvider
    {
        public override DateTimeOffset GetUtcNow() => utc;
    }
}
