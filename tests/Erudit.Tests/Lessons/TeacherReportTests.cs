using Erudit.Domain;
using Erudit.Domain.Lessons;
using Erudit.Domain.People;
using Erudit.Domain.School;
using Erudit.Infrastructure.Lessons;
using Erudit.Tests.Database;
using Microsoft.EntityFrameworkCore;

namespace Erudit.Tests.Lessons;

/// <summary>Отчёт учителя: подсчёт без базы и загрузка из базы (свой учебный год 2076).</summary>
[Collection(DatabaseCollection.Name)]
public class TeacherReportTests(TestDatabase database)
{
    private sealed class FixedTime(DateTime utc) : TimeProvider
    {
        public override DateTimeOffset GetUtcNow() => utc;
    }

    private static readonly DateOnly Day = new(2026, 10, 5);

    private static ReportStudent Kid(int id, Attendance attendance = Attendance.Present, string? className = "7а") =>
        new(id, $"Ученик {id}", className, attendance);

    private static ReportLesson Lesson(LessonStatus status, int minutes, params ReportStudent[] students) =>
        new(1, Day, new TimeOnly(14, 15), 8, "Математика", "Каб.1", status, Ended: true, minutes, students);

    [Theory]
    [InlineData(20, 0.5)]
    [InlineData(40, 1)]
    [InlineData(60, 1.5)]
    [InlineData(25, 0.63)]
    public void Часы__минуты_на_сорок_с_округлением_до_сотых(int minutes, double hours) =>
        Assert.Equal((decimal)hours, AcademicHours.FromMinutes(minutes));

    [Fact]
    public void Округляется_только_итог_а_не_каждое_занятие()
    {
        var report = TeacherReport.Build(Enumerable.Range(0, 3).Select(_ => Lesson(LessonStatus.Held, 25, Kid(1))));

        Assert.Equal(1.88m, report.TotalHours); // 75 / 40 = 1,875; по занятиям было бы 0,63 × 3 = 1,89
    }

    [Fact]
    public void Индивидуальные_и_групповые__в_часы_только_проведённые()
    {
        var report = TeacherReport.Build(
        [
            Lesson(LessonStatus.Held, 40, Kid(1)),
            Lesson(LessonStatus.Held, 60, Kid(1), Kid(2, Attendance.Absent)),
            Lesson(LessonStatus.Held, 20),
            Lesson(LessonStatus.NotHeld, 40, Kid(1)),
            Lesson(LessonStatus.Cancelled, 40, Kid(1)),
            Lesson(LessonStatus.Planned, 40, Kid(1)),
            Lesson(LessonStatus.Planned, 40, Kid(1)) with { Ended = false },
        ]);

        Assert.Equal(120, report.TotalMinutes);
        Assert.Equal(3m, report.TotalHours);
        Assert.Equal((3, 1, 1, 1, 1), (report.Held, report.NotHeld, report.Unmarked, report.Cancelled, report.Upcoming));
        Assert.Equal(5, report.Lessons.Count); // без отменённого и будущего

        Assert.Equal(1m, report.Kinds.Single(k => k.Kind == ReportLessonKind.Individual).Hours);
        Assert.Equal(1.5m, report.Kinds.Single(k => k.Kind == ReportLessonKind.Group).Hours);
        Assert.Equal(0.5m, report.Kinds.Single(k => k.Kind == ReportLessonKind.NoStudents).Hours);
        Assert.Equal(ReportLessonKind.Individual, report.Kinds[0].Kind);

        // Ученик 1 был на двух: 40 + 60; ученик 2 пропустил — часов нет, но занятие всё равно групповое.
        var first = report.Students.Single(s => s.Id == 1);
        Assert.Equal((2, 0, 2.5m), (first.Attended, first.Absent, first.Hours));
        var second = report.Students.Single(s => s.Id == 2);
        Assert.Equal((0, 1, 0m), (second.Attended, second.Absent, second.Hours));
    }

    [Fact]
    public void Классы__по_порядку_номеров_и_ученик_без_класса_в_конце()
    {
        var report = TeacherReport.Build(
        [
            Lesson(LessonStatus.Held, 40, Kid(1, className: "10"), Kid(2, className: "9а"), Kid(3, className: null), Kid(4, className: "9а"), Kid(5, className: "9б")),
        ]);

        Assert.Equal(["9а", "9б", "10", null], report.Classes.Select(c => c.ClassName));
        var nine = report.Classes[0];
        Assert.Equal((2, 2, 2m), (nine.Students, nine.Attended, nine.Hours));
    }

    [DatabaseFact]
    public async Task Загрузка__границы_периода_чужие_занятия_отказы_и_класс_на_дату()
    {
        var year = await database.CreateSandboxYearAsync(2076);
        await using var db = database.CreateContext();
        Person New(string last, Role role) => new() { LastName = last, FirstName = "Тест", Roles = [new PersonRole { Role = role }] };
        var teacher = New("Учитель2076", Role.Teacher);
        var other = New("Коллега2076", Role.Teacher);
        var (kid, gone) = (New("Ученик2076", Role.Student), New("Отказник2076", Role.Student));
        var subject = new Subject { Name = "Предмет 2076" };
        var room = new Room { Name = "Каб.2076" };
        var (sevenA, sevenB) = (new SchoolClass { AcademicYearId = year.Id, Grade = 7, Letter = "а" }, new SchoolClass { AcademicYearId = year.Id, Grade = 7, Letter = "б" });
        db.AddRange(teacher, other, kid, gone, subject, room, sevenA, sevenB);
        db.Add(new ClassMembership { Student = kid, SchoolClass = sevenA, FromDate = year.StartDate, ToDate = new DateOnly(2076, 9, 30) });
        db.Add(new ClassMembership { Student = kid, SchoolClass = sevenB, FromDate = new DateOnly(2076, 10, 1) });

        Lesson At(Person who, DateOnly date, TimeOnly time, int? actual = null) => new()
        {
            Teacher = who, Subject = subject, Room = room, StartsAt = SchoolTime.ToUtc(date, time), DurationMinutes = 40,
            ActualDurationMinutes = actual, Capacity = 5, Status = LessonStatus.Held,
            Enrollments =
            [
                new Enrollment { Student = kid, Source = EnrollmentSource.Teacher, Attendance = Attendance.Present },
                new Enrollment { Student = gone, Source = EnrollmentSource.Teacher, Status = EnrollmentStatus.Declined },
            ],
        };
        db.AddRange(
            At(teacher, new DateOnly(2076, 9, 1), new TimeOnly(0, 30)),     // первый день периода, ещё 31.08 по UTC
            At(teacher, new DateOnly(2076, 9, 30), new TimeOnly(23, 30), actual: 60), // последний день
            At(teacher, new DateOnly(2076, 10, 1), new TimeOnly(0, 30)),    // уже следующий месяц
            At(teacher, new DateOnly(2076, 8, 31), new TimeOnly(23, 30)),   // ещё предыдущий месяц
            At(other, new DateOnly(2076, 9, 15), new TimeOnly(14, 0)));     // чужое
        await db.SaveChangesAsync();

        var service = new TeacherReportService(database.CreateFactory(), new FixedTime(new DateTime(2076, 11, 1, 12, 0, 0, DateTimeKind.Utc)));

        var september = await service.LoadAsync(teacher.Id, new DateOnly(2076, 9, 1), new DateOnly(2076, 9, 30));
        Assert.Equal(2, september.Held);
        Assert.Equal(100, september.TotalMinutes); // план 40 + факт 60
        Assert.Equal(ReportLessonKind.Individual, Assert.Single(september.Kinds).Kind); // отказавшийся не считается
        var row = Assert.Single(september.Students);
        Assert.Equal((kid.Id, "7а", 2), (row.Id, row.ClassName, row.Attended));

        var both = await service.LoadAsync(teacher.Id, new DateOnly(2076, 9, 30), new DateOnly(2076, 10, 1));
        Assert.Equal(["7а", "7б"], both.Students.Select(s => s.ClassName).Order());
        Assert.Equal(1, Assert.Single(both.Classes, c => c.ClassName == "7б").Students);
    }

    /// <summary>Критерий готовности этапа 5: занятие → запись → занятие прошло → отметка → отчёт.</summary>
    [DatabaseFact]
    public async Task Полный_цикл__занятие_запись_отметка_отчёт()
    {
        await database.CreateSandboxYearAsync(2078);
        var date = new DateOnly(2078, 9, 5); // понедельник
        await using var db = database.CreateContext();
        Person New(string last, Role role) => new() { LastName = last, FirstName = "Тест", Roles = [new PersonRole { Role = role }] };
        var teacher = New("Учитель2078", Role.Teacher);
        var (came, missed) = (New("Пришёл2078", Role.Student), New("Пропустил2078", Role.Student));
        var subject = new Subject { Name = "Предмет 2078" };
        var room = new Room { Name = "Каб.2078" };
        db.AddRange(teacher, came, missed, subject, room);
        await db.SaveChangesAsync();
        var before = new FixedTime(SchoolTime.ToUtc(date, new TimeOnly(9, 0)));
        var after = new FixedTime(SchoolTime.ToUtc(date, new TimeOnly(16, 0)));
        var actor = new LessonActor(teacher.Id, IsManager: false);

        await new LessonService(db, before).CreateAsync(new LessonDraft
        {
            TeacherId = teacher.Id, SubjectId = subject.Id, RoomId = room.Id, Date = date,
            StartTime = new TimeOnly(14, 15), DurationMinutes = 40, Capacity = 2,
        }, actor, warningsConfirmed: false);
        var id = await db.Lessons.Where(l => l.TeacherId == teacher.Id).Select(l => l.Id).SingleAsync();

        var signup = new SignupService(db, before);
        await signup.SignUpAsync(id, came.Id, EditScope.OnlyThis, came.Id);
        await signup.SignUpAsync(id, missed.Id, EditScope.OnlyThis, missed.Id);

        var service = new TeacherReportService(database.CreateFactory(), after);
        var unmarked = await service.LoadAsync(teacher.Id, date, date);
        Assert.Equal((1, 0m), (unmarked.Unmarked, unmarked.TotalHours));

        await new LessonService(db, after).MarkHeldAsync(id, [missed.Id], 60, actor);

        var report = await service.LoadAsync(teacher.Id, date, date);
        Assert.Equal((0, 1, 1.5m), (report.Unmarked, report.Held, report.TotalHours));
        Assert.Equal(ReportLessonKind.Group, Assert.Single(report.Kinds).Kind);
        Assert.Equal(1.5m, report.Students.Single(s => s.Id == came.Id).Hours);
        Assert.Equal((0, 1, 0m), report.Students.Where(s => s.Id == missed.Id).Select(s => (s.Attended, s.Absent, s.Hours)).Single());
    }

    [DatabaseFact]
    public async Task Периоды__месяцы_до_текущего_новые_сверху_и_весь_год()
    {
        var year = await database.CreateSandboxYearAsync(2077);
        var service = new TeacherReportService(database.CreateFactory(), new FixedTime(new DateTime(2077, 10, 15, 12, 0, 0, DateTimeKind.Utc)));

        var periods = await service.PeriodsAsync();

        Assert.Equal(["Октябрь 2077", "Сентябрь 2077", $"Учебный год {year.Name}"], periods.Select(p => p.Label));
        Assert.Equal((new DateOnly(2077, 10, 1), new DateOnly(2077, 10, 31)), (periods[0].From, periods[0].To));
    }

    /// <summary>Сводка завуча (этап 7, шаг 2): цифры — те же, что в отчёте учителя; фильтр по предмету; уволенные — только с занятиями.</summary>
    [DatabaseFact]
    public async Task Сводка_завуча__как_отчёт_учителя__предмет__уволенные()
    {
        var year = await database.CreateSandboxYearAsync(2097);
        await using var db = database.CreateContext();
        Person New(string last, Role role, bool active = true) =>
            new() { LastName = last, FirstName = "Тест", IsActive = active, Roles = [new PersonRole { Role = role }] };
        var teacher = New("Учитель2097", Role.Teacher);
        var firedWithLessons = New("Уволен2097", Role.Teacher, active: false);
        var firedIdle = New("УволенБезЗанятий2097", Role.Teacher, active: false);
        var (a, b) = (New("УченикА2097", Role.Student), New("УченикБ2097", Role.Student));
        var (math, art) = (new Subject { Name = "Матем 2097" }, new Subject { Name = "Рисование 2097" });
        var room = new Room { Name = "Каб.2097" };
        db.AddRange(teacher, firedWithLessons, firedIdle, a, b, math, art, room);

        var day = new DateOnly(2097, 9, 10);
        Lesson At(Person who, Subject subject, int hour, LessonStatus status, params Person[] students) => new()
        {
            Teacher = who, Subject = subject, Room = room, StartsAt = SchoolTime.ToUtc(day, new TimeOnly(hour, 0)), DurationMinutes = 40,
            Capacity = 5, Status = status,
            Enrollments = [.. students.Select(s => new Enrollment { Student = s, Source = EnrollmentSource.Teacher, Attendance = Attendance.Present })],
        };
        db.AddRange(
            At(teacher, math, 9, LessonStatus.Held, a),          // индивидуальное
            At(teacher, math, 10, LessonStatus.Held, a, b),      // групповое
            At(teacher, art, 11, LessonStatus.Held),             // без учеников — только во «Всего»
            At(teacher, art, 12, LessonStatus.NotHeld, a),
            At(teacher, art, 13, LessonStatus.Cancelled, a),
            At(teacher, math, 14, LessonStatus.Planned, a),      // закончилось, не отмечено
            At(firedWithLessons, math, 15, LessonStatus.Held, b));
        await db.SaveChangesAsync();

        var service = new TeacherReportService(database.CreateFactory(), new FixedTime(SchoolTime.ToUtc(day, new TimeOnly(20, 0))));
        var all = await service.SummaryAsync(day, day, subjectId: null);

        var row = all.Single(r => r.TeacherId == teacher.Id);
        Assert.Equal("Учитель2097 Т.", row.Name);
        Assert.Equal((3, 1, 1, 1), (row.Held, row.NotHeld, row.Cancelled, row.Unmarked));
        Assert.Equal((1m, 1m, 3m), (row.IndividualHours, row.GroupHours, row.TotalHours));
        var report = await service.LoadAsync(teacher.Id, day, day);
        Assert.Equal(report.TotalMinutes, row.TotalMinutes); // та же логика, что в отчёте учителя

        Assert.False(all.Single(r => r.TeacherId == firedWithLessons.Id).IsActive);
        Assert.DoesNotContain(all, r => r.TeacherId == firedIdle.Id);
        Assert.Contains(all, r => r.IsActive && r.Held == 0); // работающие учителя без занятий тоже в списке

        var mathOnly = (await service.SummaryAsync(day, day, math.Id)).Single(r => r.TeacherId == teacher.Id);
        Assert.Equal((2, 0, 0, 1, 2m), (mathOnly.Held, mathOnly.NotHeld, mathOnly.Cancelled, mathOnly.Unmarked, mathOnly.TotalHours));

        var total = TeacherSummaryRow.Total([row, all.Single(r => r.TeacherId == firedWithLessons.Id)]);
        Assert.Equal((4, 4m), (total.Held, total.TotalHours));
    }
}
