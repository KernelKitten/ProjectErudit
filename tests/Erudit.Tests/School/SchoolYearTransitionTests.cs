using Erudit.Domain.People;
using Erudit.Domain.School;
using Erudit.Infrastructure.Max;
using Erudit.Infrastructure.School;
using Erudit.Tests.Database;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;

namespace Erudit.Tests.School;

[Collection(DatabaseCollection.Name)]
public class SchoolYearTransitionTests(TestDatabase database)
{
    private static Person Teacher(string last) => new() { LastName = last, FirstName = "Учитель", Roles = [new PersonRole { Role = Role.Teacher }] };
    private static Person Student(string last) => new() { LastName = last, FirstName = "Ученик", Roles = [new PersonRole { Role = Role.Student }] };

    [DatabaseFact]
    public async Task Перевод__классы_на_год_старше_выпускники_и_руководители()
    {
        var year = await database.CreateSandboxYearAsync(2040);
        await using (var db = database.CreateContext())
        {
            var t4 = Teacher("Четвёркина");
            var t8 = Teacher("Восьмёркин");
            var c4 = new SchoolClass { AcademicYearId = year.Id, Grade = 4, Letter = "а", ClassTeacher = t4 };
            var c8 = new SchoolClass { AcademicYearId = year.Id, Grade = 8, Letter = "б", ClassTeacher = t8 };
            var c11 = new SchoolClass { AcademicYearId = year.Id, Grade = 11, Letter = "" };
            db.SchoolClasses.AddRange(c4, c8, c11);
            foreach (var (cls, name) in new[] { (c4, "Младшая1"), (c4, "Младшая2"), (c8, "Средний"), (c11, "Выпускник") })
            {
                db.ClassMemberships.Add(new ClassMembership { Student = Student(name), SchoolClass = cls, FromDate = year.StartDate });
            }
            await db.SaveChangesAsync();
        }

        TransitionPlan plan;
        await using (var db = database.CreateContext())
        {
            var transition = new SchoolYearTransition(db);
            var preview = await transition.PreviewAsync(year.Id);
            Assert.Null(preview.Blocked);
            Assert.Equal("2041/42", preview.NextYearName);
            Assert.Equal(["1а", "5а", "9б"], preview.NewClasses.Select(c => c.Name));
            Assert.Equal(1, preview.Graduates);
            plan = await transition.ApplyAsync(year.Id);
        }

        await using var check = database.CreateContext();
        var next = await check.AcademicYears.SingleAsync(y => y.Name == "2041/42");
        var classes = await check.SchoolClasses.Include(c => c.ClassTeacher)
            .Where(c => c.AcademicYearId == next.Id).ToDictionaryAsync(c => c.Name());

        Assert.Equal("Четвёркина", classes["1а"].ClassTeacher?.LastName); // учитель 4-го — к новым первоклассникам
        Assert.Null(classes["5а"].ClassTeacherId);                         // у 5-х — вручную
        Assert.Equal("Восьмёркин", classes["9б"].ClassTeacher?.LastName);   // руководитель идёт с классом

        int Count(string name) => check.ClassMemberships.Count(m => m.SchoolClassId == classes[name].Id && m.ToDate == null);
        Assert.Equal((0, 2, 1), (Count("1а"), Count("5а"), Count("9б")));

        var graduate = await check.People.SingleAsync(p => p.LastName == "Выпускник");
        Assert.False(graduate.IsActive);
        Assert.False(await check.ClassMemberships.AnyAsync(m => m.StudentId == graduate.Id && m.ToDate == null));

        // Повторный перевод заблокирован.
        Assert.NotNull((await new SchoolYearTransition(check).PreviewAsync(year.Id)).Blocked);
        await Assert.ThrowsAsync<InvalidOperationException>(() => new SchoolYearTransition(check).ApplyAsync(year.Id));
    }

    [DatabaseFact]
    public async Task Перевод__все_девятые_объединяются_в_один_десятый()
    {
        var year = await database.CreateSandboxYearAsync(2042);
        await using (var db = database.CreateContext())
        {
            var c9a = new SchoolClass { AcademicYearId = year.Id, Grade = 9, Letter = "а", ClassTeacher = Teacher("Девяткина") };
            var c9b = new SchoolClass { AcademicYearId = year.Id, Grade = 9, Letter = "б", ClassTeacher = Teacher("Девяткин") };
            db.SchoolClasses.AddRange(c9a, c9b);
            foreach (var (cls, name) in new[] { (c9a, "Девятый1"), (c9a, "Девятый2"), (c9b, "Девятый3") })
            {
                db.ClassMemberships.Add(new ClassMembership { Student = Student(name), SchoolClass = cls, FromDate = year.StartDate });
            }
            await db.SaveChangesAsync();
        }

        await using (var db = database.CreateContext())
        {
            var transition = new SchoolYearTransition(db);
            var preview = await transition.PreviewAsync(year.Id);
            var tenth = Assert.Single(preview.NewClasses);
            Assert.Equal("10", tenth.Name);
            Assert.Equal(["9а", "9б"], tenth.FromClasses);
            Assert.Equal(3, tenth.Students);
            Assert.Null(tenth.ClassTeacherId); // 9-х было два — руководителя назначают вручную
            await transition.ApplyAsync(year.Id);
        }

        await using var check = database.CreateContext();
        var next = await check.AcademicYears.SingleAsync(y => y.Name == "2043/44");
        var cls10 = await check.SchoolClasses.SingleAsync(c => c.AcademicYearId == next.Id);
        Assert.Equal((10, ""), (cls10.Grade, cls10.Letter));
        Assert.Equal(3, await check.ClassMemberships.CountAsync(m => m.SchoolClassId == cls10.Id && m.ToDate == null));
    }

    [DatabaseFact]
    public async Task Перевод__четвёртый_в_делится_между_5а_и_5б()
    {
        var year = await database.CreateSandboxYearAsync(2049);
        await using (var db = database.CreateContext())
        {
            var c4a = new SchoolClass { AcademicYearId = year.Id, Grade = 4, Letter = "а" };
            var c4v = new SchoolClass { AcademicYearId = year.Id, Grade = 4, Letter = "в", ClassTeacher = Teacher("Вэшкина") };
            db.SchoolClasses.AddRange(c4a, c4v);
            foreach (var (cls, name) in new[] { (c4a, "Аншин"), (c4v, "Аист"), (c4v, "Бобр"), (c4v, "Волк") })
            {
                db.ClassMemberships.Add(new ClassMembership { Student = Student(name), SchoolClass = cls, FromDate = year.StartDate });
            }
            await db.SaveChangesAsync();
        }

        await using (var db = database.CreateContext())
        {
            var transition = new SchoolYearTransition(db);
            var preview = await transition.PreviewAsync(year.Id);
            Assert.Equal(["1а", "1в", "5а", "5б"], preview.NewClasses.Select(c => c.Name)); // 5в не создаётся
            Assert.Equal("Вэшкина У.", preview.NewClasses.Single(c => c.Name == "1в").ClassTeacher);

            // Поровну: в 5а уже один (из 4а) → Аист в 5б, Бобр в 5а, Волк в 5б.
            Assert.Equal(["б", "а", "б"], preview.SplitStudents.Select(s => s.SuggestedLetter));

            // Без распределения не переводим.
            await Assert.ThrowsAsync<InvalidOperationException>(() => transition.ApplyAsync(year.Id));

            var split = preview.SplitStudents.ToDictionary(s => s.StudentId, s => s.SuggestedLetter);
            split[preview.SplitStudents.Single(s => s.Name.StartsWith("Волк")).StudentId] = "а"; // завуч поправил
            await transition.ApplyAsync(year.Id, split);
        }

        await using var check = database.CreateContext();
        var next = await check.AcademicYears.SingleAsync(y => y.Name == "2050/51");
        var classes = await check.SchoolClasses.Where(c => c.AcademicYearId == next.Id).ToDictionaryAsync(c => c.Name());
        int Count(string name) => check.ClassMemberships.Count(m => m.SchoolClassId == classes[name].Id && m.ToDate == null);
        Assert.Equal((3, 1, 0), (Count("5а"), Count("5б"), Count("1в")));
    }

    [DatabaseFact]
    public async Task Перевод__единственный_девятый__руководитель_идёт_в_десятый()
    {
        var year = await database.CreateSandboxYearAsync(2047);
        await using (var db = database.CreateContext())
        {
            db.SchoolClasses.Add(new SchoolClass { AcademicYearId = year.Id, Grade = 9, Letter = "в", ClassTeacher = Teacher("Одинокий") });
            await db.SaveChangesAsync();
        }

        await using var context = database.CreateContext();
        var tenth = Assert.Single((await new SchoolYearTransition(context).PreviewAsync(year.Id)).NewClasses);
        Assert.Equal(("10", "Одинокий У."), (tenth.Name, tenth.ClassTeacher));
    }

    [DatabaseFact]
    public async Task Напоминание__только_27_08_05_09_и_пока_перевод_не_сделан()
    {
        await using (var db = database.CreateContext())
        {
            db.People.Add(new Person
            {
                LastName = "Напоминаемый", FirstName = "Админ", MaxUserId = 900_000_777,
                Roles = [new PersonRole { Role = Role.Admin }],
            });
            await db.SaveChangesAsync();
        }

        var bot = new RecordingBot();
        await using var context = database.CreateContext();
        var job = new SchoolYearReminderJob(context, bot, TimeProvider.System, Options.Create(new SiteOptions()),
            NullLogger<SchoolYearReminderJob>.Instance);

        Assert.Equal(0, await job.RunForDateAsync(new DateOnly(2045, 8, 26))); // ещё рано
        Assert.Equal(0, await job.RunForDateAsync(new DateOnly(2045, 9, 6)));  // уже поздно
        Assert.True(await job.RunForDateAsync(new DateOnly(2045, 8, 27)) > 0);  // пора: года 2045/46 с классами нет
        Assert.Contains(bot.Sent, s => s.UserId == 900_000_777 && s.Text.Contains("2045/46"));

        // Перевод сделан (в 2045/46 есть класс) — больше не напоминаем.
        var year = await database.CreateSandboxYearAsync(2045);
        await using (var db = database.CreateContext())
        {
            db.SchoolClasses.Add(new SchoolClass { AcademicYearId = year.Id, Grade = 1, Letter = "а" });
            await db.SaveChangesAsync();
        }
        Assert.Equal(0, await job.RunForDateAsync(new DateOnly(2045, 9, 2)));
    }

    private sealed class RecordingBot : IMaxBotClient
    {
        public List<(long UserId, string Text)> Sent { get; } = [];
        public Task<MaxBotInfo> GetMeAsync(CancellationToken ct = default) => throw new NotSupportedException();
        public Task<MaxUpdatesPage> GetUpdatesAsync(long? marker, int timeoutSeconds, CancellationToken ct = default) => throw new NotSupportedException();
        public Task SendMessageAsync(long userId, MaxMessage message, CancellationToken ct = default)
        {
            Sent.Add((userId, message.Text));
            return Task.CompletedTask;
        }
        public Task AnswerCallbackAsync(string callbackId, MaxMessage message, CancellationToken ct = default) => throw new NotSupportedException();
    }
}

internal static class SchoolClassTestExtensions
{
    public static string Name(this SchoolClass c) => $"{c.Grade}{c.Letter}";
}
