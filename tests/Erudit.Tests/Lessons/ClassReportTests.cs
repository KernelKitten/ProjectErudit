using Erudit.Domain;
using Erudit.Domain.Lessons;
using Erudit.Domain.People;
using Erudit.Domain.School;
using Erudit.Infrastructure.Lessons;
using Erudit.Tests.Database;

namespace Erudit.Tests.Lessons;

/// <summary>Отчёт классного руководителя (этап 7, шаг 3). Свой учебный год 2099.</summary>
[Collection(DatabaseCollection.Name)]
public class ClassReportTests(TestDatabase database)
{
    private sealed class FixedTime(DateTime utc) : TimeProvider
    {
        public override DateTimeOffset GetUtcNow() => utc;
    }

    [DatabaseFact]
    public async Task Ученики_класса__разбивка__перевод_в_другой_класс__права()
    {
        var year = await database.CreateSandboxYearAsync(2099);
        await using var db = database.CreateContext();
        Person New(string last, Role role) => new() { LastName = last, FirstName = "Тест", Roles = [new PersonRole { Role = role }] };
        var (leader, other) = (New("Руководитель2099", Role.Teacher), New("Другой2099", Role.Teacher));
        var (a, b, c) = (New("Аистов2099", Role.Student), New("Бобров2099", Role.Student), New("Волков2099", Role.Student));
        var (math, art) = (new Subject { Name = "Матем 2099" }, new Subject { Name = "Рисование 2099" });
        var room = new Room { Name = "Каб.2099" };
        var sevenA = new SchoolClass { AcademicYearId = year.Id, Grade = 7, Letter = "а", ClassTeacher = leader };
        var sevenB = new SchoolClass { AcademicYearId = year.Id, Grade = 7, Letter = "б" };
        db.AddRange(leader, other, a, b, c, math, art, room, sevenA, sevenB);
        db.AddRange(
            new ClassMembership { Student = a, SchoolClass = sevenA, FromDate = year.StartDate },
            new ClassMembership { Student = b, SchoolClass = sevenA, FromDate = year.StartDate, ToDate = new DateOnly(2099, 9, 30) },
            new ClassMembership { Student = b, SchoolClass = sevenB, FromDate = new DateOnly(2099, 10, 1) },
            new ClassMembership { Student = c, SchoolClass = sevenA, FromDate = year.StartDate });

        Lesson At(DateOnly date, Person teacher, Subject subject, LessonStatus status, params (Person Kid, Attendance Att)[] kids) => new()
        {
            Teacher = teacher, Subject = subject, Room = room, StartsAt = SchoolTime.ToUtc(date, new TimeOnly(14, 15)), DurationMinutes = 40,
            Capacity = 5, Status = status,
            Enrollments = [.. kids.Select(k => new Enrollment { Student = k.Kid, Source = EnrollmentSource.Teacher, Attendance = k.Att })],
        };
        var declined = At(new DateOnly(2099, 9, 15), leader, math, LessonStatus.Held);
        declined.Enrollments.Add(new Enrollment { Student = a, Source = EnrollmentSource.Self, Status = EnrollmentStatus.Declined });
        db.AddRange(
            At(new DateOnly(2099, 9, 10), leader, math, LessonStatus.Held, (a, Attendance.Present), (b, Attendance.Present)),
            At(new DateOnly(2099, 9, 11), other, art, LessonStatus.Held, (a, Attendance.Absent)),
            At(new DateOnly(2099, 9, 12), leader, math, LessonStatus.Cancelled, (a, Attendance.Unknown)),
            declined,
            At(new DateOnly(2099, 10, 5), leader, math, LessonStatus.Held, (a, Attendance.Present), (b, Attendance.Present)), // Б уже в 7б
            At(new DateOnly(2099, 10, 20), other, art, LessonStatus.Planned, (a, Attendance.Unknown)));                         // впереди
        await db.SaveChangesAsync();

        var service = new ClassReportService(database.CreateFactory(), new FixedTime(new DateTime(2099, 10, 15, 12, 0, 0, DateTimeKind.Utc)));
        var report = await service.LoadAsync(sevenA.Id, new DateOnly(2099, 9, 1), new DateOnly(2099, 10, 31));

        Assert.Equal(["Аистов2099 Тест", "Бобров2099 Тест", "Волков2099 Тест"], report.Rows.Select(r => r.Student.Name));
        var rowA = report.Rows[0];
        Assert.Equal(new ClassReportCounts(3, 2, 1, 1, 80), rowA.Counts);
        Assert.Equal(2m, rowA.Counts.Hours);
        Assert.Equal(["Матем 2099 · Руководитель2099 Т.", "Рисование 2099 · Другой2099 Т."], rowA.Lines.Select(l => $"{l.Subject} · {l.Teacher}"));
        Assert.Equal(new ClassReportCounts(1, 0, 1, 1, 0), rowA.Lines[1].Counts);

        var rowB = report.Rows[1];
        Assert.Equal("до 30.09", rowB.Student.Note);
        Assert.Equal(new ClassReportCounts(1, 1, 0, 0, 40), rowB.Counts); // октябрьское занятие — уже в 7б

        Assert.Empty(report.Rows[2].Lines); // никуда не ходит, но в списке есть
        Assert.Equal((4, 3, 3m), (report.Total.Held, report.Total.Attended, report.Total.Hours));

        // 7б за октябрь: Б с 01.10 и его занятие.
        var sevenBReport = await service.LoadAsync(sevenB.Id, new DateOnly(2099, 9, 1), new DateOnly(2099, 10, 31));
        var onlyB = Assert.Single(sevenBReport.Rows);
        Assert.Equal(("с 01.10", 1), (onlyB.Student.Note, onlyB.Counts.Attended));

        // Права: руководитель — только свой класс, завуч — все классы года.
        Assert.Equal([sevenA.Id], (await service.ClassesAsync(leader.Id, isManager: false)).Select(x => x.Id));
        Assert.Contains(sevenB.Id, (await service.ClassesAsync(other.Id, isManager: true)).Select(x => x.Id));
        Assert.True(await service.CanViewAsync(sevenA.Id, leader.Id, isManager: false));
        Assert.False(await service.CanViewAsync(sevenB.Id, leader.Id, isManager: false));
        Assert.True(await service.CanViewAsync(sevenB.Id, leader.Id, isManager: true));
    }
}
