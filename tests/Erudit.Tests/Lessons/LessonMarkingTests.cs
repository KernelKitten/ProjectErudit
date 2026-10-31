using Erudit.Domain;
using Erudit.Domain.Accounting;
using Erudit.Domain.Lessons;
using Erudit.Domain.People;
using Erudit.Domain.School;
using Erudit.Infrastructure.Lessons;
using Erudit.Infrastructure.Persistence;
using Erudit.Tests.Database;
using Microsoft.EntityFrameworkCore;

namespace Erudit.Tests.Lessons;

/// <summary>
/// Отметка занятий и список неотмеченных. Каждый тест заводит своего учителя и работает в своём
/// далёком году (2065–2069); «сейчас» задаём сами — занятие либо уже прошло, либо ещё нет.
/// </summary>
[Collection(DatabaseCollection.Name)]
public class LessonMarkingTests(TestDatabase database)
{
    private static readonly TimeOnly Lesson8 = new(14, 15);

    private sealed class FixedTime(DateTime utc) : TimeProvider
    {
        public override DateTimeOffset GetUtcNow() => utc;
    }

    private sealed record World(int TeacherId, int OtherTeacherId, int SubjectId, int RoomId, List<int> StudentIds);

    private async Task<World> CreateWorldAsync(string tag)
    {
        await using var db = database.CreateContext();
        Person Person(string last, Role role) => new() { LastName = last, FirstName = "Тест", Roles = [new PersonRole { Role = role }] };
        var teacher = Person($"Учитель{tag}", Role.Teacher);
        var other = Person($"Коллега{tag}", Role.Teacher);
        var subject = new Subject { Name = $"Предмет {tag}" };
        var room = new Room { Name = $"Каб.{tag}" };
        var kids = Enumerable.Range(1, 3).Select(i => Person($"Ученик{tag}{i}", Role.Student)).ToList();
        db.AddRange(teacher, other, subject, room);
        db.AddRange(kids);
        await db.SaveChangesAsync();
        return new World(teacher.Id, other.Id, subject.Id, room.Id, kids.Select(k => k.Id).ToList());
    }

    /// <summary>Понедельник первой недели сентября — рабочий день без исключений в далёком году.</summary>
    private static DateOnly Monday(int year) => new DateOnly(year, 9, 1).AddDays(((int)DayOfWeek.Monday - (int)new DateOnly(year, 9, 1).DayOfWeek + 7) % 7);

    /// <summary>Разовое занятие на 40 минут с тремя учениками.</summary>
    private async Task<int> CreateLessonAsync(World w, DateOnly date)
    {
        await using var db = database.CreateContext();
        var draft = new LessonDraft
        {
            TeacherId = w.TeacherId, SubjectId = w.SubjectId, RoomId = w.RoomId, Date = date,
            LessonNumber = 8, StartTime = Lesson8, DurationMinutes = 40, Capacity = 5, StudentIds = w.StudentIds,
        };
        await new LessonService(db, TimeProvider.System).CreateAsync(draft, new LessonActor(w.TeacherId, false), warningsConfirmed: false);
        return await db.Lessons.Where(l => l.TeacherId == w.TeacherId).OrderByDescending(l => l.Id).Select(l => l.Id).FirstAsync();
    }

    private static LessonService ServiceAt(EruditDbContext db, DateOnly date, TimeOnly time) =>
        new(db, new FixedTime(SchoolTime.ToUtc(date, time)));

    private static LessonActor As(World w) => new(w.TeacherId, IsManager: false);

    private async Task<Lesson> LoadAsync(int id)
    {
        await using var db = database.CreateContext();
        return await db.Lessons.Include(l => l.Enrollments).SingleAsync(l => l.Id == id);
    }

    [DatabaseFact]
    public async Task Проведено__посещаемость_и_факт_затем_исправление_и_снятие()
    {
        var w = await CreateWorldAsync("Отметка");
        var date = Monday(2065);
        var id = await CreateLessonAsync(w, date);
        await using var db = database.CreateContext();
        var service = ServiceAt(db, date, new TimeOnly(15, 0));

        await service.MarkHeldAsync(id, [w.StudentIds[0]], 60, As(w));

        var held = await LoadAsync(id);
        Assert.Equal(LessonStatus.Held, held.Status);
        Assert.Equal(60, held.ActualDurationMinutes);
        Assert.Equal(60, held.ReportedMinutes);
        Assert.Equal(Attendance.Absent, held.Enrollments.Single(e => e.StudentId == w.StudentIds[0]).Attendance);
        Assert.Equal(Attendance.Present, held.Enrollments.Single(e => e.StudentId == w.StudentIds[1]).Attendance);

        // Исправление: все были, длительность по плану — факт не храним.
        await service.MarkHeldAsync(id, [], 40, As(w));
        var fixedMark = await LoadAsync(id);
        Assert.Null(fixedMark.ActualDurationMinutes);
        Assert.All(fixedMark.Enrollments, e => Assert.Equal(Attendance.Present, e.Attendance));

        // «Не состоялось» и снятие отметки сбрасывают посещаемость.
        await service.MarkNotHeldAsync(id, As(w));
        var notHeld = await LoadAsync(id);
        Assert.Equal(LessonStatus.NotHeld, notHeld.Status);
        Assert.All(notHeld.Enrollments, e => Assert.Equal(Attendance.Unknown, e.Attendance));

        await service.UnmarkAsync(id, As(w));
        Assert.Equal(LessonStatus.Planned, (await LoadAsync(id)).Status);
    }

    [DatabaseFact]
    public async Task Отказавшийся_ученик_без_отметки_посещаемости()
    {
        var w = await CreateWorldAsync("Отказ");
        var date = Monday(2066);
        var id = await CreateLessonAsync(w, date);
        await using var db = database.CreateContext();
        var declined = await db.Enrollments.SingleAsync(e => e.LessonId == id && e.StudentId == w.StudentIds[2]);
        declined.Status = EnrollmentStatus.Declined;
        await db.SaveChangesAsync();

        await ServiceAt(db, date, new TimeOnly(15, 0)).MarkHeldAsync(id, [], 40, As(w));

        var held = await LoadAsync(id);
        Assert.Equal(Attendance.Unknown, held.Enrollments.Single(e => e.StudentId == w.StudentIds[2]).Attendance);
        Assert.Equal(2, held.Enrollments.Count(e => e.Attendance == Attendance.Present));
    }

    [DatabaseFact]
    public async Task Нельзя__до_начала_чужое_отменённое_закрытый_месяц_и_неверная_длительность()
    {
        var w = await CreateWorldAsync("НельзяОтм");
        var date = Monday(2067);
        var id = await CreateLessonAsync(w, date);
        await using var db = database.CreateContext();
        var after = ServiceAt(db, date, new TimeOnly(15, 0));

        var early = await Assert.ThrowsAsync<InvalidOperationException>(() =>
            ServiceAt(db, date, new TimeOnly(14, 0)).MarkHeldAsync(id, [], 40, As(w)));
        Assert.Contains("ещё не началось", early.Message);
        // С момента начала — уже можно.
        await ServiceAt(db, date, Lesson8).MarkHeldAsync(id, [], 40, As(w));
        await after.UnmarkAsync(id, As(w));

        await Assert.ThrowsAsync<InvalidOperationException>(() => after.MarkHeldAsync(id, [], 0, As(w)));
        var foreign = await Assert.ThrowsAsync<InvalidOperationException>(() =>
            after.MarkHeldAsync(id, [], 40, new LessonActor(w.OtherTeacherId, false)));
        Assert.Contains("только свои", foreign.Message);
        await after.MarkHeldAsync(id, [], 40, new LessonActor(w.OtherTeacherId, IsManager: true)); // завуч может
        await after.UnmarkAsync(id, As(w));

        await after.CancelAsync(id, EditScope.OnlyThis, null, As(w));
        await Assert.ThrowsAsync<InvalidOperationException>(() => after.MarkHeldAsync(id, [], 40, As(w)));
        await after.RestoreAsync(id, As(w), warningsConfirmed: true);

        db.ClosedPeriods.Add(new ClosedPeriod { Month = new DateOnly(date.Year, date.Month, 1), ClosedById = w.OtherTeacherId, ClosedAt = DateTime.UtcNow });
        await db.SaveChangesAsync();
        var closed = await Assert.ThrowsAsync<InvalidOperationException>(() => after.MarkHeldAsync(id, [], 40, As(w)));
        Assert.Contains("Период закрыт", closed.Message);
    }

    [DatabaseFact]
    public async Task Неотмеченные__только_закончившиеся_запланированные_и_не_в_закрытом_месяце()
    {
        var w = await CreateWorldAsync("Неотмеч");
        var date = Monday(2068);
        var finished = await CreateLessonAsync(w, date);
        var marked = await CreateLessonAsync(w, date.AddDays(1));
        var future = await CreateLessonAsync(w, date.AddDays(14));
        var closedMonth = date.AddDays(-91); // ровно 13 недель назад — тоже понедельник, другой месяц
        var inClosed = await CreateLessonAsync(w, closedMonth);

        await using var db = database.CreateContext();
        var now = date.AddDays(2);
        await ServiceAt(db, now, new TimeOnly(10, 0)).MarkHeldAsync(marked, [], 40, As(w));
        db.ClosedPeriods.Add(new ClosedPeriod { Month = new DateOnly(closedMonth.Year, closedMonth.Month, 1), ClosedById = w.OtherTeacherId, ClosedAt = DateTime.UtcNow });
        await db.SaveChangesAsync();

        var service = new TeacherWeekService(database.CreateFactory(), new FixedTime(SchoolTime.ToUtc(now, new TimeOnly(10, 0))));
        var unmarked = await service.UnmarkedAsync(w.TeacherId);

        Assert.Equal(finished, Assert.Single(unmarked).Id);
        Assert.DoesNotContain(unmarked, l => l.Id == future || l.Id == inClosed);

        // Идущее сейчас занятие ещё не «неотмеченное».
        var during = new TeacherWeekService(database.CreateFactory(), new FixedTime(SchoolTime.ToUtc(date, new TimeOnly(14, 30))));
        Assert.Empty(await during.UnmarkedAsync(w.TeacherId));
    }
}
