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
/// Изменение, отмена и возврат занятий. Каждый тест заводит своего учителя, кабинеты и учеников
/// и работает в своём далёком году (2056–2063), поэтому не пересекается с другими тестами.
/// </summary>
[Collection(DatabaseCollection.Name)]
public class LessonEditTests(TestDatabase database)
{
    private static readonly TimeOnly Lesson8 = new(14, 15);

    private sealed record World(int TeacherId, int OtherTeacherId, int SubjectId, int RoomId, int OtherRoomId, List<int> StudentIds);

    private async Task<World> CreateWorldAsync(string tag, int students = 3)
    {
        await using var db = database.CreateContext();
        Person Person(string last, Role role) => new() { LastName = last, FirstName = "Тест", Roles = [new PersonRole { Role = role }] };
        var teacher = Person($"Учитель{tag}", Role.Teacher);
        var other = Person($"Коллега{tag}", Role.Teacher);
        var subject = new Subject { Name = $"Предмет {tag}" };
        var room = new Room { Name = $"Каб.{tag}" };
        var otherRoom = new Room { Name = $"Каб.{tag}-2" };
        var kids = Enumerable.Range(1, students).Select(i => Person($"Ученик{tag}{i}", Role.Student)).ToList();
        db.AddRange(teacher, other, subject, room, otherRoom);
        db.AddRange(kids);
        await db.SaveChangesAsync();
        return new World(teacher.Id, other.Id, subject.Id, room.Id, otherRoom.Id, kids.Select(k => k.Id).ToList());
    }

    private static DateOnly NextDay(DayOfWeek day, DateOnly from) => from.AddDays(((int)day - (int)from.DayOfWeek + 7) % 7);

    private static LessonDraft Draft(World w, DateOnly date) => new()
    {
        TeacherId = w.TeacherId, SubjectId = w.SubjectId, RoomId = w.RoomId, Date = date,
        LessonNumber = 8, StartTime = Lesson8, DurationMinutes = 40, Capacity = 5,
    };

    private static LessonActor As(World w) => new(w.TeacherId, IsManager: false);

    private static LessonService Service(EruditDbContext db) => new(db, TimeProvider.System);

    /// <summary>Серия по вторникам: 6 занятий, первые двое учеников записаны на всю серию.</summary>
    private async Task<(DateOnly First, List<Lesson> Lessons)> CreateTuesdaysAsync(World w, int year)
    {
        var first = NextDay(DayOfWeek.Tuesday, new DateOnly(year, 9, 1));
        await using var db = database.CreateContext();
        var draft = Draft(w, first) with
        {
            RepeatDays = [DayOfWeek.Tuesday], RepeatUntil = first.AddDays(35), StudentIds = w.StudentIds.Take(2).ToList(),
        };
        Assert.Equal(6, await Service(db).CreateAsync(draft, As(w), warningsConfirmed: false));
        return (first, await LessonsAsync(w));
    }

    private async Task<List<Lesson>> LessonsAsync(World w)
    {
        await using var db = database.CreateContext();
        return await db.Lessons.Include(l => l.Enrollments).Where(l => l.TeacherId == w.TeacherId).OrderBy(l => l.StartsAt).ThenBy(l => l.Id)
            .ToListAsync();
    }

    /// <summary>Черновик «как есть» для занятия: дата, время и ученики из базы.</summary>
    private static LessonDraft AsIs(World w, Lesson l) => Draft(w, DateOnly.FromDateTime(SchoolTime.ToSchool(l.StartsAt))) with
    {
        StudentIds = l.Enrollments.Where(e => e.Status == EnrollmentStatus.Active).Select(e => e.StudentId).ToList(),
    };

    [DatabaseFact]
    public async Task Только_это__меняется_одно_занятие_и_остаётся_в_серии()
    {
        var w = await CreateWorldAsync("ТолькоЭто");
        var (_, lessons) = await CreateTuesdaysAsync(w, 2056);
        var third = lessons[2];

        await using var db = database.CreateContext();
        var draft = AsIs(w, third) with { RoomId = w.OtherRoomId, LessonNumber = null, StartTime = new TimeOnly(15, 0) };
        Assert.Equal(1, await Service(db).UpdateAsync(third.Id, draft, EditScope.OnlyThis, As(w), warningsConfirmed: false));

        var after = await LessonsAsync(w);
        Assert.Equal(w.OtherRoomId, after[2].RoomId);
        Assert.Equal(new TimeOnly(15, 0), TimeOnly.FromDateTime(SchoolTime.ToSchool(after[2].StartsAt)));
        Assert.Equal(third.SeriesId, after[2].SeriesId);
        Assert.All(after.Where((_, i) => i != 2), l => Assert.Equal(w.RoomId, l.RoomId));
    }

    [DatabaseFact]
    public async Task Все_последующие__серия_делится_прошлые_не_трогаем()
    {
        var w = await CreateWorldAsync("Последующие");
        var (first, lessons) = await CreateTuesdaysAsync(w, 2057);
        var third = lessons[2];
        var thirdDate = first.AddDays(14);

        await using var db = database.CreateContext();
        var draft = AsIs(w, third) with { RoomId = w.OtherRoomId, Topic = "Новая тема" };
        Assert.Equal(4, await Service(db).UpdateAsync(third.Id, draft, EditScope.ThisAndFollowing, As(w), warningsConfirmed: false));

        var after = await LessonsAsync(w);
        var series = await db.Series.Where(s => s.TeacherId == w.TeacherId).OrderBy(s => s.Id).ToListAsync();
        Assert.Equal(2, series.Count);
        Assert.Equal(thirdDate.AddDays(-1), series[0].ToDate);
        Assert.Equal(thirdDate, series[1].FromDate);
        Assert.Equal(w.OtherRoomId, series[1].RoomId);
        Assert.Equal(2, await db.SeriesEnrollments.CountAsync(e => e.SeriesId == series[1].Id));

        Assert.All(after.Take(2), l => Assert.Equal((series[0].Id, w.RoomId), (l.SeriesId!.Value, l.RoomId)));
        Assert.All(after.Skip(2), l => Assert.Equal((series[1].Id, w.OtherRoomId, "Новая тема"), (l.SeriesId!.Value, l.RoomId, l.Topic)));
    }

    [DatabaseFact]
    public async Task Все_последующие_со_сменой_дня__старые_отменены_новые_по_новому_дню()
    {
        var w = await CreateWorldAsync("СменаДня");
        var (first, lessons) = await CreateTuesdaysAsync(w, 2058);
        var thursday = first.AddDays(16); // четверг на неделе третьего занятия

        await using var db = database.CreateContext();
        var draft = AsIs(w, lessons[2]) with { Date = thursday };
        var check = await Service(db).CheckUpdateAsync(lessons[2].Id, draft, EditScope.ThisAndFollowing, As(w));
        Assert.True(check.CanCreate);
        Assert.Equal(3, check.Dates.Count); // четверги до конца серии (последний вторник + 35 дней)
        Assert.Equal(3, await Service(db).UpdateAsync(lessons[2].Id, draft, EditScope.ThisAndFollowing, As(w), warningsConfirmed: false));

        var after = await LessonsAsync(w);
        var cancelled = after.Where(l => l.Status == LessonStatus.Cancelled).ToList();
        Assert.Equal(4, cancelled.Count);
        Assert.All(cancelled, l => Assert.Equal(LessonService.MovedReason, l.CancelReason));
        var thursdays = after.Where(l => l.Status == LessonStatus.Planned && SchoolTime.ToSchool(l.StartsAt).DayOfWeek == DayOfWeek.Thursday).ToList();
        Assert.Equal(3, thursdays.Count);
        Assert.All(thursdays, l => Assert.Equal(2, l.Enrollments.Count(e => e.Status == EnrollmentStatus.Active)));
    }

    [DatabaseFact]
    public async Task Ученики__изменение_переносится_в_серию_отказ_сохраняется()
    {
        var w = await CreateWorldAsync("Ученики", students: 3);
        var (_, lessons) = await CreateTuesdaysAsync(w, 2059);
        int a = w.StudentIds[0], b = w.StudentIds[1], c = w.StudentIds[2];

        await using var db = database.CreateContext();
        // Ученик A отказался от 4-го занятия.
        var declined = await db.Enrollments.SingleAsync(e => e.LessonId == lessons[3].Id && e.StudentId == a);
        declined.Status = EnrollmentStatus.Declined;
        await db.SaveChangesAsync();

        // В 3-м занятии учитель убирает B и добавляет C — «это и все последующие».
        var draft = AsIs(w, lessons[2]) with { StudentIds = [a, c] };
        await Service(db).UpdateAsync(lessons[2].Id, draft, EditScope.ThisAndFollowing, As(w), warningsConfirmed: false);

        var after = await LessonsAsync(w);
        EnrollmentStatus? Status(Lesson l, int id) => l.Enrollments.FirstOrDefault(e => e.StudentId == id)?.Status;
        Assert.Equal(EnrollmentStatus.Declined, Status(after[3], a));
        Assert.Equal(EnrollmentStatus.Removed, Status(after[3], b));
        Assert.Equal(EnrollmentStatus.Active, Status(after[3], c));
        Assert.Equal(EnrollmentStatus.Active, Status(after[1], b)); // прошлые занятия не меняются
        Assert.Null(Status(after[1], c));

        var newSeries = after[2].SeriesId!.Value;
        var enrolled = await db.SeriesEnrollments.Where(e => e.SeriesId == newSeries).Select(e => e.StudentId).OrderBy(x => x).ToListAsync();
        Assert.Equal(new[] { a, c }.Order(), enrolled);
    }

    [DatabaseFact]
    public async Task Первое_занятие_все_последующие__серия_правится_на_месте()
    {
        var w = await CreateWorldAsync("Первое");
        var (_, lessons) = await CreateTuesdaysAsync(w, 2060);

        await using var db = database.CreateContext();
        var draft = AsIs(w, lessons[0]) with { RoomId = w.OtherRoomId };
        Assert.Equal(6, await Service(db).UpdateAsync(lessons[0].Id, draft, EditScope.ThisAndFollowing, As(w), warningsConfirmed: false));

        var series = await db.Series.Where(s => s.TeacherId == w.TeacherId).ToListAsync();
        Assert.Equal(w.OtherRoomId, Assert.Single(series).RoomId);
        Assert.All(await LessonsAsync(w), l => Assert.Equal(w.OtherRoomId, l.RoomId));
    }

    [DatabaseFact]
    public async Task Отмена_и_возврат()
    {
        var w = await CreateWorldAsync("Отмена");
        var (first, lessons) = await CreateTuesdaysAsync(w, 2061);

        await using var db = database.CreateContext();
        // Только 2-е, с причиной.
        Assert.Equal(1, await Service(db).CancelAsync(lessons[1].Id, EditScope.OnlyThis, " болезнь ", As(w)));
        // 4-е и все последующие: серия заканчивается накануне.
        Assert.Equal(3, await Service(db).CancelAsync(lessons[3].Id, EditScope.ThisAndFollowing, null, As(w)));

        var after = await LessonsAsync(w);
        Assert.Equal("болезнь", after[1].CancelReason);
        Assert.Equal([LessonStatus.Planned, LessonStatus.Cancelled, LessonStatus.Planned, LessonStatus.Cancelled,
            LessonStatus.Cancelled, LessonStatus.Cancelled], after.Select(l => l.Status));
        Assert.Equal(first.AddDays(20), (await db.Series.SingleAsync(s => s.TeacherId == w.TeacherId)).ToDate);

        // Вернуть 2-е можно.
        await Service(db).RestoreAsync(lessons[1].Id, As(w), warningsConfirmed: false);
        var restored = await db.Lessons.AsNoTracking().SingleAsync(l => l.Id == lessons[1].Id);
        Assert.Equal((LessonStatus.Planned, (string?)null), (restored.Status, restored.CancelReason));

        // На время 4-го учитель уже поставил другое занятие — вернуть нельзя.
        await Service(db).CreateAsync(Draft(w, first.AddDays(21)), As(w), warningsConfirmed: false);
        var check = await Service(db).CheckRestoreAsync(lessons[3].Id, As(w));
        Assert.Contains(check.Errors, e => e.Contains("уже есть занятие"));
    }

    [DatabaseFact]
    public async Task Завуч__создаёт_и_меняет_за_учителя_а_в_журнале_автор_завуч()
    {
        var w = await CreateWorldAsync("Завуч");
        var manager = new LessonActor(w.OtherTeacherId, IsManager: true);
        var date = NextDay(DayOfWeek.Monday, new DateOnly(2063, 9, 1));

        await using var db = database.CreateContext();
        db.CurrentPersonId = w.OtherTeacherId;
        Assert.Equal(1, await Service(db).CreateAsync(Draft(w, date), manager, warningsConfirmed: false));

        var lesson = Assert.Single(await LessonsAsync(w));
        Assert.Equal(w.TeacherId, lesson.TeacherId); // занятие — учителя

        var draft = AsIs(w, lesson) with { RoomId = w.OtherRoomId };
        Assert.Equal(1, await Service(db).UpdateAsync(lesson.Id, draft, EditScope.OnlyThis, manager, warningsConfirmed: false));

        var id = lesson.Id.ToString();
        var authors = await db.AuditEntries.Where(a => a.EntityType == nameof(Lesson) && a.EntityId == id)
            .Select(a => a.PersonId).ToListAsync();
        Assert.Equal(2, authors.Count); // создание и изменение
        Assert.All(authors, a => Assert.Equal(w.OtherTeacherId, a)); // а автор — завуч
    }

    [DatabaseFact]
    public async Task Уволенный_учитель__создать_нельзя_отменить_можно_и_он_в_списке()
    {
        var w = await CreateWorldAsync("Уволен");
        var manager = new LessonActor(w.OtherTeacherId, IsManager: true);
        var (_, lessons) = await CreateTuesdaysAsync(w, 2064);

        await using var db = database.CreateContext();
        var teacher = await db.People.SingleAsync(p => p.Id == w.TeacherId);
        teacher.IsActive = false;
        await db.SaveChangesAsync();

        var free = NextDay(DayOfWeek.Monday, new DateOnly(2064, 9, 1));
        Assert.Contains((await Service(db).CheckAsync(Draft(w, free), manager)).Errors, e => e.Contains("уволился"));
        Assert.Equal(6, await Service(db).CancelAsync(lessons[0].Id, EditScope.ThisAndFollowing, "учитель уволился", manager));

        var teachers = await new LessonFormService(database.CreateFactory()).TeachersAsync();
        Assert.False(teachers.Single(t => t.Id == w.TeacherId).IsActive);
        Assert.True(teachers.Single(t => t.Id == w.OtherTeacherId).IsActive);
    }

    [DatabaseFact]
    public async Task Нельзя__отмеченное_чужое_закрытый_месяц()
    {
        var w = await CreateWorldAsync("Нельзя");
        var (_, lessons) = await CreateTuesdaysAsync(w, 2062);

        await using var db = database.CreateContext();
        var held = await db.Lessons.SingleAsync(l => l.Id == lessons[0].Id);
        held.Status = LessonStatus.Held;
        db.ClosedPeriods.Add(new ClosedPeriod
        {
            Month = new DateOnly(2062, SchoolTime.ToSchool(lessons[5].StartsAt).Month, 1), ClosedById = w.OtherTeacherId, ClosedAt = DateTime.UtcNow,
        });
        await db.SaveChangesAsync();

        Assert.Contains((await Service(db).CheckUpdateAsync(lessons[0].Id, AsIs(w, lessons[0]), EditScope.OnlyThis, As(w))).Errors,
            e => e.Contains("уже отмечено"));
        Assert.Contains((await Service(db).CheckCancelAsync(lessons[1].Id, EditScope.OnlyThis, new LessonActor(w.OtherTeacherId, false))).Errors,
            e => e.Contains("только свои"));
        Assert.True((await Service(db).CheckCancelAsync(lessons[1].Id, EditScope.OnlyThis, new LessonActor(w.OtherTeacherId, true))).CanCreate);
        Assert.Contains((await Service(db).CheckCancelAsync(lessons[5].Id, EditScope.OnlyThis, As(w))).Errors,
            e => e.Contains("Период закрыт"));
    }
}
