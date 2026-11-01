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
/// Запись учеников и родителей. Каждый тест работает в своём далёком учебном году (2070–2082) со своими людьми;
/// «сейчас» — понедельник 1 сентября года теста, занятия — позже.
/// </summary>
[Collection(DatabaseCollection.Name)]
public class SignupServiceTests(TestDatabase database)
{
    private static readonly TimeOnly Lesson8 = new(14, 15);

    private sealed class FixedTime(DateTime utc) : TimeProvider
    {
        public override DateTimeOffset GetUtcNow() => utc;
    }

    private sealed record World(int Year, int TeacherId, int SubjectId, int RoomId, int ClassGrade, List<int> Students, int ParentId, int StrangerId);

    /// <summary>Учитель, предмет, кабинет, класс нужной параллели с тремя учениками; у первого — родитель.</summary>
    private async Task<World> CreateWorldAsync(int year, int grade = 7)
    {
        var academicYear = await database.CreateSandboxYearAsync(year);
        await using var db = database.CreateContext();
        var tag = year.ToString();
        Person New(string last, Role role) => new() { LastName = last, FirstName = "Тест", Roles = [new PersonRole { Role = role }] };
        var teacher = New($"Учитель{tag}", Role.Teacher);
        var subject = new Subject { Name = $"Предмет {tag}" };
        var room = new Room { Name = $"Каб.{tag}" };
        var kids = Enumerable.Range(1, 3).Select(i => New($"Ученик{tag}{i}", Role.Student)).ToList();
        var parent = New($"Родитель{tag}", Role.Parent);
        var stranger = New($"Чужой{tag}", Role.Parent);
        var schoolClass = new SchoolClass { AcademicYearId = academicYear.Id, Grade = grade, Letter = "а" };
        db.AddRange(teacher, subject, room, parent, stranger, schoolClass);
        db.AddRange(kids);
        db.AddRange(kids.Select(k => new ClassMembership { Student = k, SchoolClass = schoolClass, FromDate = academicYear.StartDate }));
        db.Add(new ParentLink { Parent = parent, Student = kids[0] });
        await db.SaveChangesAsync();
        return new World(year, teacher.Id, subject.Id, room.Id, grade, kids.Select(k => k.Id).ToList(), parent.Id, stranger.Id);
    }

    private static DateOnly FirstMonday(int year, int weeks = 1)
    {
        var first = new DateOnly(year, 9, 1);
        return first.AddDays(((int)DayOfWeek.Monday - (int)first.DayOfWeek + 7) % 7 + 7 * weeks);
    }

    private static SignupService ServiceAt(EruditDbContext db, World w) =>
        new(db, new FixedTime(SchoolTime.ToUtc(FirstMonday(w.Year, 0), new TimeOnly(9, 0))));

    private async Task<int> CreateLessonAsync(World w, DateOnly date, int capacity = 5, bool selfSignup = true,
        int? gradeFrom = null, int? gradeTo = null, TimeOnly? time = null, int? teacherId = null)
    {
        await using var db = database.CreateContext();
        var draft = new LessonDraft
        {
            TeacherId = teacherId ?? w.TeacherId, SubjectId = w.SubjectId, RoomId = w.RoomId, Date = date, LessonNumber = 8,
            StartTime = time ?? Lesson8, DurationMinutes = 40, Capacity = capacity,
            SelfSignup = selfSignup, SignupGradeFrom = gradeFrom, SignupGradeTo = gradeTo,
        };
        await new LessonService(db, TimeProvider.System).CreateAsync(draft, new LessonActor(draft.TeacherId, false), warningsConfirmed: true);
        return await db.Lessons.Where(l => l.TeacherId == draft.TeacherId).OrderByDescending(l => l.Id).Select(l => l.Id).FirstAsync();
    }

    /// <summary>Серия по понедельникам на 4 занятия; возвращает занятия по порядку.</summary>
    private async Task<List<int>> CreateSeriesAsync(World w, int capacity = 5)
    {
        var from = FirstMonday(w.Year);
        await using var db = database.CreateContext();
        var draft = new LessonDraft
        {
            TeacherId = w.TeacherId, SubjectId = w.SubjectId, RoomId = w.RoomId, Date = from, RepeatDays = [DayOfWeek.Monday],
            RepeatUntil = from.AddDays(21), LessonNumber = 8, StartTime = Lesson8, DurationMinutes = 40, Capacity = capacity,
        };
        await new LessonService(db, TimeProvider.System).CreateAsync(draft, new LessonActor(w.TeacherId, false), warningsConfirmed: true);
        return await db.Lessons.Where(l => l.TeacherId == w.TeacherId).OrderBy(l => l.StartsAt).Select(l => l.Id).ToListAsync();
    }

    private async Task<List<Enrollment>> EnrollmentsAsync(int studentId)
    {
        await using var db = database.CreateContext();
        return await db.Enrollments.Include(e => e.Lesson).Where(e => e.StudentId == studentId).OrderBy(e => e.Lesson.StartsAt).ToListAsync();
    }

    [DatabaseFact]
    public async Task Запись_и_отказ__сам_и_родитель_чужому_нельзя()
    {
        var w = await CreateWorldAsync(2070);
        var id = await CreateLessonAsync(w, FirstMonday(2070));
        await using var db = database.CreateContext();
        var service = ServiceAt(db, w);

        await service.SignUpAsync(id, w.Students[0], EditScope.OnlyThis, w.Students[0]);
        var own = Assert.Single(await EnrollmentsAsync(w.Students[0]));
        Assert.Equal(EnrollmentSource.Self, own.Source);

        // Отказ, затем запись родителем — та же строка, источник обновился.
        await service.DeclineAsync(id, w.Students[0], EditScope.OnlyThis, w.Students[0]);
        Assert.Equal(EnrollmentStatus.Declined, Assert.Single(await EnrollmentsAsync(w.Students[0])).Status);
        await using (var again = database.CreateContext())
        {
            await ServiceAt(again, w).SignUpAsync(id, w.Students[0], EditScope.OnlyThis, w.ParentId);
        }
        var back = Assert.Single(await EnrollmentsAsync(w.Students[0]));
        Assert.Equal(EnrollmentStatus.Active, back.Status);
        Assert.Equal(EnrollmentSource.Parent, back.Source);

        await Assert.ThrowsAsync<InvalidOperationException>(() => service.SignUpAsync(id, w.Students[1], EditScope.OnlyThis, w.StrangerId));
        await Assert.ThrowsAsync<InvalidOperationException>(() => service.SignUpAsync(id, w.Students[1], EditScope.OnlyThis, w.ParentId));
        await Assert.ThrowsAsync<InvalidOperationException>(() => service.DeclineAsync(id, w.Students[0], EditScope.OnlyThis, w.StrangerId));
    }

    [DatabaseFact]
    public async Task Последнее_место__занимает_один_из_двух_одновременных()
    {
        var w = await CreateWorldAsync(2071);
        var id = await CreateLessonAsync(w, FirstMonday(2071), capacity: 1);

        async Task<bool> TryAsync(int studentId)
        {
            await using var db = database.CreateContext();
            try
            {
                await ServiceAt(db, w).SignUpAsync(id, studentId, EditScope.OnlyThis, studentId);
                return true;
            }
            catch (InvalidOperationException ex)
            {
                Assert.Equal(SignupService.NoSeats, ex.Message);
                return false;
            }
        }

        var results = await Task.WhenAll(w.Students.Select(s => Task.Run(() => TryAsync(s))));

        Assert.Equal(1, results.Count(r => r));
        await using var check = database.CreateContext();
        Assert.Equal(1, await check.Enrollments.CountAsync(e => e.LessonId == id && e.Status == EnrollmentStatus.Active));
    }

    [DatabaseFact]
    public async Task Нельзя__началось_пересечение_закрыто_и_не_та_параллель()
    {
        var w = await CreateWorldAsync(2072, grade: 7);
        var date = FirstMonday(2072);
        await using var db = database.CreateContext();
        var service = ServiceAt(db, w);
        var student = w.Students[0];

        var noSelf = await CreateLessonAsync(w, date, selfSignup: false, time: new TimeOnly(9, 0));
        var older = await CreateLessonAsync(w, date, gradeFrom: 9, gradeTo: 11, time: new TimeOnly(10, 0));
        var fits = await CreateLessonAsync(w, date, gradeFrom: 5, gradeTo: 8, time: Lesson8);
        await Assert.ThrowsAsync<InvalidOperationException>(() => service.SignUpAsync(noSelf, student, EditScope.OnlyThis, student));
        await Assert.ThrowsAsync<InvalidOperationException>(() => service.SignUpAsync(older, student, EditScope.OnlyThis, student));
        await service.SignUpAsync(fits, student, EditScope.OnlyThis, student);

        // Занятие другого учителя в то же время — ученик занят.
        Person other;
        await using (var add = database.CreateContext())
        {
            other = new Person { LastName = "Коллега2072", FirstName = "Тест", Roles = [new PersonRole { Role = Role.Teacher }] };
            add.Add(other);
            await add.SaveChangesAsync();
        }
        var overlap = await CreateLessonAsync(w, date, time: Lesson8.AddMinutes(20), teacherId: other.Id);
        var ex = await Assert.ThrowsAsync<InvalidOperationException>(() => service.SignUpAsync(overlap, student, EditScope.OnlyThis, student));
        Assert.Contains("другое занятие", ex.Message);

        // Занятие уже началось: и запись, и отказ — поздно.
        var late = new SignupService(database.CreateContext(), new FixedTime(SchoolTime.ToUtc(date, Lesson8.AddMinutes(30))));
        await Assert.ThrowsAsync<InvalidOperationException>(() => late.SignUpAsync(overlap, w.Students[1], EditScope.OnlyThis, w.Students[1]));
        await Assert.ThrowsAsync<InvalidOperationException>(() => late.DeclineAsync(fits, student, EditScope.OnlyThis, student));

        // Закрытый месяц.
        db.Add(new ClosedPeriod { Month = new DateOnly(date.Year, date.Month, 1), ClosedById = w.TeacherId, ClosedAt = DateTime.UtcNow });
        await db.SaveChangesAsync();
        await Assert.ThrowsAsync<InvalidOperationException>(() => service.SignUpAsync(overlap, w.Students[1], EditScope.OnlyThis, w.Students[1]));
    }

    [DatabaseFact]
    public async Task Убранного_учителем_сам_не_записывается__отказавшийся_возвращается_даже_в_закрытое()
    {
        var w = await CreateWorldAsync(2073);
        var id = await CreateLessonAsync(w, FirstMonday(2073));
        await using var db = database.CreateContext();
        var service = ServiceAt(db, w);

        db.Enrollments.Add(new Enrollment { LessonId = id, StudentId = w.Students[0], Status = EnrollmentStatus.Removed, Source = EnrollmentSource.Teacher });
        db.Enrollments.Add(new Enrollment { LessonId = id, StudentId = w.Students[1], Status = EnrollmentStatus.Declined, Source = EnrollmentSource.Teacher });
        var lesson = await db.Lessons.SingleAsync(l => l.Id == id);
        lesson.SelfSignup = false;
        await db.SaveChangesAsync();

        await Assert.ThrowsAsync<InvalidOperationException>(() => service.SignUpAsync(id, w.Students[0], EditScope.OnlyThis, w.Students[0]));
        await service.SignUpAsync(id, w.Students[1], EditScope.OnlyThis, w.Students[1]);
        await Assert.ThrowsAsync<InvalidOperationException>(() => service.SignUpAsync(id, w.Students[2], EditScope.OnlyThis, w.Students[2]));
    }

    [DatabaseFact]
    public async Task Серия__на_все_следующие_с_пропуском_и_отказ_от_всех_следующих()
    {
        var w = await CreateWorldAsync(2074);
        var lessons = await CreateSeriesAsync(w, capacity: 1);
        await using var db = database.CreateContext();
        var service = ServiceAt(db, w);
        var (first, second) = (w.Students[0], w.Students[1]);

        // Третье занятие занял другой ученик.
        await service.SignUpAsync(lessons[2], second, EditScope.OnlyThis, second);

        var result = await service.SignUpAsync(lessons[1], first, EditScope.ThisAndFollowing, first);
        Assert.Equal(2, result.Count); // 2-е и 4-е
        var skipped = Assert.Single(result.Skipped);
        Assert.Contains(SignupService.NoSeats, skipped);

        var seriesId = (await db.Lessons.SingleAsync(l => l.Id == lessons[1])).SeriesId;
        Assert.True(await db.SeriesEnrollments.AnyAsync(e => e.SeriesId == seriesId && e.StudentId == first));
        var enrolled = await EnrollmentsAsync(first);
        Assert.Equal([lessons[1], lessons[3]], enrolled.Select(e => e.LessonId));
        Assert.All(enrolled, e => Assert.Equal(EnrollmentSource.Series, e.Source));

        // Отказ от одного — запись на серию остаётся; от всех следующих — серия снята.
        Assert.Equal(1, await service.DeclineAsync(lessons[1], first, EditScope.OnlyThis, first));
        Assert.True(await db.SeriesEnrollments.AnyAsync(e => e.SeriesId == seriesId && e.StudentId == first));
        await service.SignUpAsync(lessons[1], first, EditScope.OnlyThis, first);
        Assert.Equal(2, await service.DeclineAsync(lessons[1], first, EditScope.ThisAndFollowing, first));
        Assert.False(await db.SeriesEnrollments.AsNoTracking().AnyAsync(e => e.SeriesId == seriesId && e.StudentId == first));
        Assert.All(await EnrollmentsAsync(first), e => Assert.Equal(EnrollmentStatus.Declined, e.Status));
    }

    [DatabaseFact]
    public async Task Страница__видно_своё_и_открытое_по_параллели_и_причину_занятости()
    {
        var w = await CreateWorldAsync(2075, grade: 7);
        var date = FirstMonday(2075);
        var open = await CreateLessonAsync(w, date, capacity: 1, time: new TimeOnly(9, 0));
        var closed = await CreateLessonAsync(w, date, selfSignup: false, time: new TimeOnly(10, 0));
        var older = await CreateLessonAsync(w, date, gradeFrom: 9, time: new TimeOnly(11, 0));
        var mine = await CreateLessonAsync(w, date, time: new TimeOnly(12, 0));
        await using var db = database.CreateContext();
        var service = ServiceAt(db, w);
        var (student, rival) = (w.Students[0], w.Students[1]);
        await service.SignUpAsync(mine, student, EditScope.OnlyThis, student);
        await service.SignUpAsync(open, rival, EditScope.OnlyThis, rival);

        var from = SchoolTime.ToUtc(date, TimeOnly.MinValue);
        var data = await service.LoadAsync(student, student, from, from.AddDays(7));

        Assert.Equal(mine, Assert.Single(data.Mine).Id);
        Assert.True(data.Mine[0].CanDecline);
        var row = Assert.Single(data.Open);
        Assert.Equal(open, row.Id);
        Assert.Equal(SignupService.NoSeats, row.Blocked);
        Assert.DoesNotContain(data.Open, l => l.Id == closed || l.Id == older);

        await Assert.ThrowsAsync<InvalidOperationException>(() => service.LoadAsync(student, w.StrangerId, from, from.AddDays(7)));
        Assert.Equal(mine, Assert.Single((await service.LoadAsync(student, w.ParentId, from, from.AddDays(7))).Mine).Id);
    }

    [Fact]
    public void Параллель__границы_и_ученик_без_класса()
    {
        var lesson = new Lesson { SignupGradeFrom = 5, SignupGradeTo = 9 };
        Assert.True(lesson.IsOpenForGrade(5));
        Assert.True(lesson.IsOpenForGrade(9));
        Assert.False(lesson.IsOpenForGrade(4));
        Assert.False(lesson.IsOpenForGrade(10));
        Assert.False(lesson.IsOpenForGrade(null));
        Assert.True(new Lesson().IsOpenForGrade(null));
        Assert.False(new Lesson { SelfSignup = false }.IsOpenForGrade(7));
        Assert.True(new Lesson { SignupGradeTo = 4 }.IsOpenForGrade(1));
    }
}
