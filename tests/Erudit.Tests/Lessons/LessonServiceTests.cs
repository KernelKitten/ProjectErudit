using Erudit.Domain;
using Erudit.Domain.Accounting;
using Erudit.Domain.Calendar;
using Erudit.Domain.Lessons;
using Erudit.Domain.People;
using Erudit.Domain.School;
using Erudit.Infrastructure.Lessons;
using Erudit.Infrastructure.Persistence;
using Erudit.Tests.Database;
using Microsoft.EntityFrameworkCore;

namespace Erudit.Tests.Lessons;

/// <summary>
/// Каждый тест заводит своего учителя и свой кабинет и работает в далёких годах (2052–2055),
/// поэтому конфликты не пересекаются с тестовыми данными и другими тестами.
/// </summary>
[Collection(DatabaseCollection.Name)]
public class LessonServiceTests(TestDatabase database)
{
    private static readonly TimeOnly Lesson8 = new(14, 15);

    private sealed record World(int TeacherId, int OtherTeacherId, int SubjectId, int RoomId, List<int> StudentIds);

    /// <summary>Учитель, ещё один учитель, предмет, кабинет и ученики — всё новое, с уникальными именами.</summary>
    private async Task<World> CreateWorldAsync(string tag, int students = 3)
    {
        await using var db = database.CreateContext();
        Person Person(string last, Role role) => new() { LastName = last, FirstName = "Тест", Roles = [new PersonRole { Role = role }] };
        var teacher = Person($"Учитель{tag}", Role.Teacher);
        var other = Person($"Коллега{tag}", Role.Teacher);
        var subject = new Subject { Name = $"Предмет {tag}" };
        var room = new Room { Name = $"Каб.{tag}" };
        var kids = Enumerable.Range(1, students).Select(i => Person($"Ученик{tag}{i}", Role.Student)).ToList();
        db.AddRange(teacher, other, subject, room);
        db.AddRange(kids);
        await db.SaveChangesAsync();
        return new World(teacher.Id, other.Id, subject.Id, room.Id, kids.Select(k => k.Id).ToList());
    }

    private static LessonDraft Draft(World w, DateOnly date, TimeOnly? start = null, int minutes = 40) => new()
    {
        TeacherId = w.TeacherId,
        SubjectId = w.SubjectId,
        RoomId = w.RoomId,
        Date = date,
        LessonNumber = 8,
        StartTime = start ?? Lesson8,
        DurationMinutes = minutes,
        Capacity = 5,
    };

    private static LessonActor As(World w) => new(w.TeacherId, IsManager: false);

    private LessonService Service(EruditDbContext db) => new(db, TimeProvider.System);

    [DatabaseFact]
    public async Task Разовое_занятие__создаётся_с_учениками()
    {
        var w = await CreateWorldAsync("Разовое");
        await using var db = database.CreateContext();
        var draft = Draft(w, new DateOnly(2054, 9, 7)) with { StudentIds = w.StudentIds, Topic = "  Дроби  " };

        var check = await Service(db).CheckAsync(draft, As(w));
        Assert.True(check.CanCreate);
        Assert.Empty(check.Warnings);
        Assert.Equal(1, await Service(db).CreateAsync(draft, As(w), warningsConfirmed: false));

        var lesson = await db.Lessons.Include(l => l.Enrollments).SingleAsync(l => l.TeacherId == w.TeacherId);
        Assert.Null(lesson.SeriesId);
        Assert.Equal("Дроби", lesson.Topic);
        Assert.Equal(SchoolTime.ToUtc(new DateOnly(2054, 9, 7), Lesson8), lesson.StartsAt);
        Assert.Equal(3, lesson.Enrollments.Count);
        Assert.All(lesson.Enrollments, e => Assert.Equal(EnrollmentSource.Teacher, e.Source));
    }

    [DatabaseFact]
    public async Task Повторение_вторник_и_четверг__две_серии_праздник_пропущен() // серии создаются с понедельника
    {
        var w = await CreateWorldAsync("Серия", students: 2);
        await using var db = database.CreateContext();
        db.CalendarDays.Add(new CalendarDay { Date = new DateOnly(2054, 9, 15), IsWorkingDay = false }); // вторник — праздник
        await db.SaveChangesAsync();

        // С понедельника 07.09 по пятницу 02.10: вторников 4 (один праздник), четвергов 4.
        var draft = Draft(w, new DateOnly(2054, 9, 7)) with
        {
            RepeatDays = [DayOfWeek.Thursday, DayOfWeek.Tuesday],
            RepeatUntil = new DateOnly(2054, 10, 2),
            StudentIds = w.StudentIds,
        };
        var check = await Service(db).CheckAsync(draft, As(w));
        Assert.Equal(7, check.Dates.Count);
        Assert.DoesNotContain(new DateOnly(2054, 9, 15), check.Dates);
        Assert.Equal(7, await Service(db).CreateAsync(draft, As(w), warningsConfirmed: false));

        var series = await db.Series.Where(s => s.TeacherId == w.TeacherId).OrderBy(s => s.Id).ToListAsync();
        Assert.Equal([DayOfWeek.Tuesday, DayOfWeek.Thursday], series.Select(s => s.DayOfWeek));
        Assert.Equal(4, await db.SeriesEnrollments.CountAsync(e => e.Series.TeacherId == w.TeacherId)); // 2 серии × 2 ученика
        var lessons = await db.Lessons.Include(l => l.Enrollments).Where(l => l.TeacherId == w.TeacherId).ToListAsync();
        Assert.Equal(7, lessons.Count);
        Assert.All(lessons, l => Assert.NotNull(l.SeriesId));
        Assert.All(lessons.SelectMany(l => l.Enrollments), e => Assert.Equal(EnrollmentSource.Series, e.Source));
    }

    [DatabaseFact]
    public async Task Учитель_занят__нельзя_а_отменённое_занятие_не_мешает()
    {
        var w = await CreateWorldAsync("Занят");
        var date = new DateOnly(2054, 9, 8);
        await using var db = database.CreateContext();
        await Service(db).CreateAsync(Draft(w, date), As(w), false);

        // Пересекается на 20 минут (14:35–15:15 и 14:15–14:55), даже в другом кабинете.
        var otherRoom = db.Rooms.Add(new Room { Name = "Каб.ЗанятДругой" }).Entity;
        await db.SaveChangesAsync();
        var overlapping = Draft(w, date, new TimeOnly(14, 35)) with { RoomId = otherRoom.Id };
        var check = await Service(db).CheckAsync(overlapping, As(w));
        Assert.False(check.CanCreate);
        Assert.Contains(check.Errors, e => e.Contains("у учителя уже есть занятие"));
        await Assert.ThrowsAsync<InvalidOperationException>(() => Service(db).CreateAsync(overlapping, As(w), true));

        // Сразу после окончания (14:55) — можно.
        Assert.True((await Service(db).CheckAsync(Draft(w, date, new TimeOnly(14, 55)), As(w))).CanCreate);

        // Первое занятие отменили — время свободно.
        var first = await db.Lessons.SingleAsync(l => l.TeacherId == w.TeacherId);
        first.Status = LessonStatus.Cancelled;
        await db.SaveChangesAsync();
        Assert.True((await Service(db).CheckAsync(overlapping, As(w))).CanCreate);
    }

    [DatabaseFact]
    public async Task Кабинет_занят_другим_учителем__предупреждение_и_подтверждение()
    {
        var w = await CreateWorldAsync("Кабинет");
        var date = new DateOnly(2054, 9, 9);
        await using var db = database.CreateContext();
        var colleague = new LessonActor(w.OtherTeacherId, IsManager: false);
        await Service(db).CreateAsync(Draft(w, date) with { TeacherId = w.OtherTeacherId }, colleague, false);

        var check = await Service(db).CheckAsync(Draft(w, date), As(w));
        Assert.True(check.CanCreate);
        var warning = Assert.Single(check.Warnings);
        Assert.Contains($"Коллега{"Кабинет"} Т.", warning);

        await Assert.ThrowsAsync<InvalidOperationException>(() => Service(db).CreateAsync(Draft(w, date), As(w), warningsConfirmed: false));
        Assert.Equal(1, await Service(db).CreateAsync(Draft(w, date), As(w), warningsConfirmed: true));
    }

    [DatabaseFact]
    public async Task Нерабочий_день_для_разового__предупреждение()
    {
        var w = await CreateWorldAsync("Выходной");
        await using var db = database.CreateContext();
        var saturday = new DateOnly(2054, 9, 12);
        var check = await Service(db).CheckAsync(Draft(w, saturday), As(w));
        Assert.True(check.CanCreate);
        Assert.Contains(check.Warnings, x => x.Contains("нерабочий день"));
    }

    [DatabaseFact]
    public async Task Закрытый_месяц__нельзя()
    {
        var w = await CreateWorldAsync("Закрыт");
        await using var db = database.CreateContext();
        db.ClosedPeriods.Add(new ClosedPeriod { Month = new DateOnly(2055, 3, 1), ClosedById = w.OtherTeacherId, ClosedAt = DateTime.UtcNow });
        await db.SaveChangesAsync();

        var check = await Service(db).CheckAsync(Draft(w, new DateOnly(2055, 3, 1)), As(w));
        Assert.False(check.CanCreate);
        Assert.Contains(check.Errors, e => e.Contains("Период закрыт"));
        Assert.True((await Service(db).CheckAsync(Draft(w, new DateOnly(2055, 4, 1)), As(w))).CanCreate);
    }

    [DatabaseFact]
    public async Task Права_и_места()
    {
        var w = await CreateWorldAsync("Права", students: 3);
        await using var db = database.CreateContext();
        var draft = Draft(w, new DateOnly(2054, 9, 10));

        // Коллега не может создать занятие за другого учителя, а завуч может.
        Assert.False((await Service(db).CheckAsync(draft, new LessonActor(w.OtherTeacherId, false))).CanCreate);
        Assert.True((await Service(db).CheckAsync(draft, new LessonActor(w.OtherTeacherId, true))).CanCreate);

        // Учеников больше, чем мест.
        var crowded = draft with { Capacity = 2, StudentIds = w.StudentIds };
        Assert.Contains((await Service(db).CheckAsync(crowded, As(w))).Errors, e => e.Contains("больше, чем мест"));
    }

    [DatabaseFact]
    public async Task Повторять_до__четверть_полугодие_год()
    {
        var year = await database.CreateSandboxYearAsync(2052);
        await using var db = database.CreateContext();
        db.Quarters.AddRange(
            new Quarter { AcademicYearId = year.Id, Number = 1, StartDate = new(2052, 9, 2), EndDate = new(2052, 10, 27) },
            new Quarter { AcademicYearId = year.Id, Number = 2, StartDate = new(2052, 11, 6), EndDate = new(2052, 12, 28) },
            new Quarter { AcademicYearId = year.Id, Number = 3, StartDate = new(2053, 1, 12), EndDate = new(2053, 3, 20) },
            new Quarter { AcademicYearId = year.Id, Number = 4, StartDate = new(2053, 3, 31), EndDate = new(2053, 5, 31) });
        await db.SaveChangesAsync();

        var september = await Service(db).GetRepeatEndsAsync(new DateOnly(2052, 9, 10));
        Assert.Equal([new DateOnly(2052, 10, 27), new DateOnly(2052, 12, 28), new DateOnly(2053, 5, 31)], september.Select(r => r.Date));

        // Осенние каникулы: ближайшая — 2-я четверть, она же конец полугодия.
        var vacation = await Service(db).GetRepeatEndsAsync(new DateOnly(2052, 10, 30));
        Assert.Equal(["До конца 2-й четверти", "До конца учебного года"], vacation.Select(r => r.Label));
    }
}

public class QuarterRulesTests
{
    private static readonly AcademicYear Year = new() { Name = "2026/27", StartDate = new(2026, 9, 1), EndDate = new(2027, 8, 31) };

    private static readonly Quarter[] Others =
    [
        new() { Number = 1, StartDate = new(2026, 9, 1), EndDate = new(2026, 10, 25) },
        new() { Number = 3, StartDate = new(2027, 1, 11), EndDate = new(2027, 3, 21) },
    ];

    [Fact]
    public void Правильная_четверть__ошибок_нет() =>
        Assert.Null(QuarterRules.Validate(2, new(2026, 11, 4), new(2026, 12, 28), Year, Others));

    [Theory]
    [InlineData(1, "2026-11-04", "2026-12-28", "уже есть")]
    [InlineData(2, "2026-10-20", "2026-12-28", "Пересекается")]
    [InlineData(2, "2027-04-01", "2027-05-31", "по порядку")]
    [InlineData(4, "2027-05-01", "2027-09-10", "внутри учебного года")]
    [InlineData(2, "2026-12-28", "2026-11-04", "раньше")]
    [InlineData(5, "2027-04-01", "2027-05-31", "от 1 до 4")]
    public void Ошибки(int number, string start, string end, string expected)
    {
        var error = QuarterRules.Validate(number, DateOnly.Parse(start), DateOnly.Parse(end), Year, Others);
        Assert.Contains(expected, error);
    }
}
