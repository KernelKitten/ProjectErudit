using System.Text.Json.Nodes;
using Erudit.Domain;
using Erudit.Domain.Lessons;
using Erudit.Domain.People;
using Erudit.Domain.School;
using Erudit.Infrastructure.Auth;
using Erudit.Infrastructure.Max;
using Erudit.Infrastructure.Notifications;
using Erudit.Tests.Database;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;

namespace Erudit.Tests.Notifications;

/// <summary>Команды бота «Сегодня» / «На неделю». Свой учебный год у каждого теста с базой (2093–2094).</summary>
[Collection(DatabaseCollection.Name)]
public class BotScheduleTests(TestDatabase database)
{
    private sealed record World(int TeacherId, int SubjectId, int RoomId, int KidId, int ParentId, DateOnly Monday);

    /// <summary>Учитель, ученик 7а и его родитель (без «Все уведомления» — команды доступны любому родителю); у всех есть MAX.</summary>
    private async Task<World> CreateWorldAsync(int year)
    {
        var academicYear = await database.CreateSandboxYearAsync(year);
        await using var db = database.CreateContext();
        var next = year * 1000L + 500;
        Person New(string last, Role role) => new() { LastName = last, FirstName = "Тест", MaxUserId = ++next, Roles = [new PersonRole { Role = role }] };
        var teacher = New($"Учитель{year}", Role.Teacher);
        var kid = New($"Ученик{year}", Role.Student);
        var parent = New($"Родитель{year}", Role.Parent);
        var subject = new Subject { Name = $"Предмет {year}" };
        var room = new Room { Name = $"Каб.{year}" };
        var schoolClass = new SchoolClass { AcademicYearId = academicYear.Id, Grade = 7, Letter = "а" };
        db.AddRange(teacher, kid, parent, subject, room, schoolClass);
        db.Add(new ClassMembership { Student = kid, SchoolClass = schoolClass, FromDate = academicYear.StartDate });
        db.Add(new ParentLink { Parent = parent, Student = kid });
        await db.SaveChangesAsync();
        var first = new DateOnly(year, 9, 1);
        var monday = first.AddDays(((int)DayOfWeek.Monday - (int)first.DayOfWeek + 7) % 7 + 7);
        return new World(teacher.Id, subject.Id, room.Id, kid.Id, parent.Id, monday);
    }

    private async Task AddLessonAsync(World w, DateOnly date, TimeOnly start, LessonStatus status = LessonStatus.Planned,
        bool withKid = false, int? lessonNumber = null)
    {
        await using var db = database.CreateContext();
        var lesson = new Lesson
        {
            TeacherId = w.TeacherId, SubjectId = w.SubjectId, RoomId = w.RoomId, StartsAt = SchoolTime.ToUtc(date, start),
            DurationMinutes = 40, LessonNumber = lessonNumber, Capacity = 5, Status = status,
        };
        if (withKid)
        {
            lesson.Enrollments.Add(new Enrollment { StudentId = w.KidId, Source = EnrollmentSource.Teacher });
        }
        db.Add(lesson);
        await db.SaveChangesAsync();
    }

    /// <summary>Человек пишет боту <paramref name="text"/> в момент <paramref name="nowUtc"/>; возвращает ответ бота.</summary>
    private async Task<MaxMessage> AskAsync(int personId, string text, DateTime nowUtc)
    {
        await using var db = database.CreateContext();
        var maxUserId = await db.People.Where(p => p.Id == personId).Select(p => p.MaxUserId!.Value).SingleAsync();
        var bot = new FakeBot();
        var time = new FixedTime(nowUtc);
        var handler = new BotUpdateHandler(db, new PersonRegistration(db, time), bot, Options.Create(new MaxOptions { BotToken = "test" }),
            time, NullLogger<BotUpdateHandler>.Instance);
        await handler.HandleAsync(new JsonObject
        {
            ["update_type"] = "message_created",
            ["message"] = new JsonObject
            {
                ["sender"] = new JsonObject { ["user_id"] = maxUserId },
                ["body"] = new JsonObject { ["text"] = text },
            },
        });
        return Assert.Single(bot.Sent);
    }

    [DatabaseFact]
    public async Task Сегодня__ученик_родитель_и_учитель__с_отменённым_и_кнопками()
    {
        var w = await CreateWorldAsync(2093);
        var wednesday = w.Monday.AddDays(2);
        await AddLessonAsync(w, wednesday, new TimeOnly(14, 15), withKid: true, lessonNumber: 8);
        await AddLessonAsync(w, wednesday, new TimeOnly(15, 15), LessonStatus.Cancelled, withKid: true);
        await AddLessonAsync(w, wednesday.AddDays(1), new TimeOnly(14, 15)); // завтра — в «Сегодня» не попадает
        var noon = SchoolTime.ToUtc(wednesday, new TimeOnly(12, 0));

        var kid = await AskAsync(w.KidId, "/today", noon);
        Assert.StartsWith("📅 Сегодня, ср ", kid.Text);
        Assert.Contains("\n\nВы записаны:\n• 8 урок (14:15–14:55) — Предмет 2093, Каб.2093, учитель Учитель2093 Т.", kid.Text);
        Assert.Contains("\n• ❌ отменено: 15:15–15:55 — Предмет 2093", kid.Text);
        Assert.DoesNotContain("Вы ведёте", kid.Text);
        Assert.Equal("🚫 Не смогу: 14:15 Предмет 2093", Assert.Single(kid.Buttons!.SelectMany(r => r)).Text); // у отменённого кнопки нет

        var parent = await AskAsync(w.ParentId, "Сегодня", noon);
        Assert.Contains("\n\nУченик2093 Тест:\n• 8 урок", parent.Text);
        Assert.Equal("🚫 Тест не сможет: 14:15 Предмет 2093", Assert.Single(parent.Buttons!.SelectMany(r => r)).Text);

        var teacher = await AskAsync(w.TeacherId, "/today@test_bot", noon);
        Assert.Contains("\n\nВы ведёте:\n• 8 урок (14:15–14:55) — Предмет 2093, Каб.2093. Записаны (1 из 5): Ученик2093 Т", teacher.Text);
        Assert.Contains("\n• ❌ отменено: 15:15–15:55 — Предмет 2093, Каб.2093\n", teacher.Text + "\n");
        Assert.Null(teacher.Buttons);

        // Занятие уже началось — кнопки «Не смогу» нет.
        Assert.Null((await AskAsync(w.KidId, "/today", SchoolTime.ToUtc(wednesday, new TimeOnly(14, 20)))).Buttons);
    }

    [DatabaseFact]
    public async Task На_неделю__до_воскресенья__в_выходные_следующая__и_подсказка()
    {
        var w = await CreateWorldAsync(2094);
        await AddLessonAsync(w, w.Monday, new TimeOnly(14, 15), LessonStatus.Held, withKid: true);
        await AddLessonAsync(w, w.Monday.AddDays(3), new TimeOnly(14, 15), withKid: true, lessonNumber: 8);
        await AddLessonAsync(w, w.Monday.AddDays(7), new TimeOnly(14, 15)); // следующий понедельник

        var tuesday = await AskAsync(w.TeacherId, "/week", SchoolTime.ToUtc(w.Monday.AddDays(1), new TimeOnly(12, 0)));
        Assert.StartsWith($"🗓 Занятия с вт {w.Monday.AddDays(1):dd.MM} по вс {w.Monday.AddDays(6):dd.MM}", tuesday.Text);
        Assert.Contains($"\n• чт {w.Monday.AddDays(3):dd.MM}, 8 урок (14:15–14:55) — Предмет 2094, Каб.2094, записано 1 из 5", tuesday.Text);
        Assert.DoesNotContain("✅", tuesday.Text); // понедельник уже прошёл
        Assert.Null(tuesday.Buttons); // в «На неделю» кнопок нет

        var monday = await AskAsync(w.KidId, "неделя", SchoolTime.ToUtc(w.Monday, new TimeOnly(18, 0)));
        Assert.Contains($"\n• ✅ пн {w.Monday:dd.MM}, 14:15–14:55 — Предмет 2094", monday.Text);

        var saturday = await AskAsync(w.TeacherId, "/week", SchoolTime.ToUtc(w.Monday.AddDays(5), new TimeOnly(10, 0)));
        Assert.StartsWith($"🗓 Занятия с пн {w.Monday.AddDays(7):dd.MM}", saturday.Text);
        Assert.Contains("записано 0 из 5", saturday.Text);

        var kidSaturday = await AskAsync(w.KidId, "/week", SchoolTime.ToUtc(w.Monday.AddDays(5), new TimeOnly(10, 0)));
        Assert.EndsWith(": дополнительных занятий нет.", kidSaturday.Text);

        Assert.Contains("/today — занятия на сегодня", (await AskAsync(w.KidId, "привет", DateTime.UtcNow)).Text);
    }

    [Theory]
    [InlineData("/today", false)]
    [InlineData("today", false)]
    [InlineData(" Сегодня ", false)]
    [InlineData("/week@id231107791231_bot", true)]
    [InlineData("На неделю", true)]
    [InlineData("неделя", true)]
    public void Команды_распознаются(string text, bool week)
    {
        Assert.True(BotSchedule.TryParse(text, out var isWeek));
        Assert.Equal(week, isWeek);
    }

    [Theory]
    [InlineData("привет")]
    [InlineData("")]
    [InlineData(null)]
    [InlineData("/start")]
    public void Не_команды(string? text) => Assert.False(BotSchedule.TryParse(text, out _));

    [Theory]
    [InlineData("2026-11-02", "2026-11-02", "2026-11-08")] // пн → пн–вс
    [InlineData("2026-11-06", "2026-11-06", "2026-11-08")] // пт → пт–вс
    [InlineData("2026-11-07", "2026-11-09", "2026-11-15")] // сб → следующая неделя
    [InlineData("2026-11-08", "2026-11-09", "2026-11-15")] // вс → следующая неделя
    public void Границы_недели(string today, string from, string to) =>
        Assert.Equal((DateOnly.Parse(from), DateOnly.Parse(to)), BotSchedule.WeekRange(DateOnly.Parse(today)));

    private sealed class FixedTime(DateTime utc) : TimeProvider
    {
        public override DateTimeOffset GetUtcNow() => utc;
    }

    private sealed class FakeBot : IMaxBotClient
    {
        public List<MaxMessage> Sent { get; } = [];
        public Task<MaxBotInfo> GetMeAsync(CancellationToken ct = default) => throw new NotSupportedException();
        public Task SetCommandsAsync(IReadOnlyList<MaxBotCommand> commands, CancellationToken ct = default) => Task.CompletedTask;
        public Task<MaxUpdatesPage> GetUpdatesAsync(long? marker, int timeoutSeconds, CancellationToken ct = default) => throw new NotSupportedException();
        public Task AnswerCallbackAsync(string callbackId, MaxMessage message, CancellationToken ct = default) => throw new NotSupportedException();

        public Task SendMessageAsync(long userId, MaxMessage message, CancellationToken ct = default)
        {
            Sent.Add(message);
            return Task.CompletedTask;
        }
    }
}
