using System.Text.Json.Nodes;
using Erudit.Domain;
using Erudit.Domain.Lessons;
using Erudit.Domain.Notifications;
using Erudit.Domain.People;
using Erudit.Domain.School;
using Erudit.Infrastructure.Auth;
using Erudit.Infrastructure.Max;
using Erudit.Infrastructure.Notifications;
using Erudit.Infrastructure.School;
using Erudit.Tests.Database;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;

namespace Erudit.Tests.Notifications;

/// <summary>Ежеминутная задача: напоминания, неотмеченные, сводка родителю, кнопки. Свой учебный год у каждого теста (2087–2092).</summary>
[Collection(DatabaseCollection.Name)]
public class ReminderTests(TestDatabase database)
{
    private sealed record World(int TeacherId, int SubjectId, int RoomId, int KidId, int ParentAllId, int ParentSummaryId, DateOnly Monday);

    /// <summary>Учитель, ученик 7а и два его родителя (со всеми уведомлениями и только со сводкой); у всех есть MAX.</summary>
    private async Task<World> CreateWorldAsync(int year)
    {
        var academicYear = await database.CreateSandboxYearAsync(year);
        await using var db = database.CreateContext();
        var next = year * 1000L;
        Person New(string last, Role role) => new() { LastName = last, FirstName = "Тест", MaxUserId = ++next, Roles = [new PersonRole { Role = role }] };
        var teacher = New($"Учитель{year}", Role.Teacher);
        var kid = New($"Ученик{year}", Role.Student);
        var parentAll = New($"РодительВсе{year}", Role.Parent);
        var parentSummary = New($"РодительСводка{year}", Role.Parent);
        var subject = new Subject { Name = $"Предмет {year}" };
        var room = new Room { Name = $"Каб.{year}" };
        var schoolClass = new SchoolClass { AcademicYearId = academicYear.Id, Grade = 7, Letter = "а" };
        db.AddRange(teacher, kid, parentAll, parentSummary, subject, room, schoolClass);
        db.Add(new ClassMembership { Student = kid, SchoolClass = schoolClass, FromDate = academicYear.StartDate });
        db.Add(new ParentLink { Parent = parentAll, Student = kid, NotifyAll = true });
        db.Add(new ParentLink { Parent = parentSummary, Student = kid });
        await db.SaveChangesAsync();
        var first = new DateOnly(year, 9, 1);
        var monday = first.AddDays(((int)DayOfWeek.Monday - (int)first.DayOfWeek + 7) % 7 + 7);
        return new World(teacher.Id, subject.Id, room.Id, kid.Id, parentAll.Id, parentSummary.Id, monday);
    }

    /// <summary>Занятие прямо в базе (без LessonService — чтобы не было мгновенных сообщений).</summary>
    private async Task AddLessonAsync(World w, DateOnly date, TimeOnly start, LessonStatus status = LessonStatus.Planned,
        Attendance? kid = null, int? lessonNumber = null)
    {
        await using var db = database.CreateContext();
        var lesson = new Lesson
        {
            TeacherId = w.TeacherId, SubjectId = w.SubjectId, RoomId = w.RoomId, StartsAt = SchoolTime.ToUtc(date, start),
            DurationMinutes = 40, LessonNumber = lessonNumber, Capacity = 5, Status = status,
        };
        if (kid is { } attendance)
        {
            lesson.Enrollments.Add(new Enrollment { StudentId = w.KidId, Source = EnrollmentSource.Teacher, Attendance = attendance });
        }
        db.Add(lesson);
        await db.SaveChangesAsync();
    }

    private async Task RunAsync(DateOnly date, TimeOnly at)
    {
        await using var db = database.CreateContext();
        var job = new NotificationScheduleJob(db, TimeProvider.System, Options.Create(new SiteOptions { BaseUrl = "https://test.example/" }),
            NullLogger<NotificationScheduleJob>.Instance);
        await job.RunAtAsync(SchoolTime.ToUtc(date, at));
    }

    private async Task<List<string>> TextsAsync(int personId)
    {
        await using var db = database.CreateContext();
        return await db.Notifications.Where(n => n.RecipientId == personId).OrderBy(n => n.Id).Select(n => n.Text).ToListAsync();
    }

    [DatabaseFact]
    public async Task Ученик__накануне_и_утром_одним_сообщением__без_дублей_и_опоздавших()
    {
        var w = await CreateWorldAsync(2087);
        var wednesday = w.Monday.AddDays(2);
        await AddLessonAsync(w, wednesday, new TimeOnly(14, 15), kid: Attendance.Unknown, lessonNumber: 8);
        await AddLessonAsync(w, wednesday, new TimeOnly(7, 30), kid: Attendance.Unknown);
        await AddLessonAsync(w, wednesday, new TimeOnly(16, 0), LessonStatus.Cancelled, kid: Attendance.Unknown);

        await RunAsync(wednesday.AddDays(-1), new TimeOnly(17, 5));
        await RunAsync(wednesday.AddDays(-1), new TimeOnly(17, 6)); // повторный запуск — не дубль
        await RunAsync(wednesday.AddDays(-1), new TimeOnly(17, 40)); // позже 30 минут — уже не шлём

        var evening = Assert.Single(await TextsAsync(w.KidId));
        Assert.StartsWith("🔔 Завтра, ср", evening);
        var lines = evening.Split('\n')[1..];
        Assert.Equal(2, lines.Length); // отменённого нет
        Assert.StartsWith("• 7:30–8:10 —", lines[0]);
        Assert.StartsWith("• 8 урок (14:15–14:55) —", lines[1]);
        Assert.Contains($"• Ученик2087 Тест: 7:30–8:10", Assert.Single(await TextsAsync(w.ParentAllId)));
        Assert.Empty(await TextsAsync(w.ParentSummaryId));

        await RunAsync(wednesday, new TimeOnly(7, 1));
        var morning = (await TextsAsync(w.KidId)).Last();
        Assert.StartsWith("🔔 Сегодня, ср", morning);
        Assert.Equal(3, morning.Split('\n').Length);
    }

    [DatabaseFact]
    public async Task Учитель__за_2_часа_и_за_15_минут__раньше_7_утра_только_за_15()
    {
        var w = await CreateWorldAsync(2088);
        var day = w.Monday.AddDays(1);
        await AddLessonAsync(w, day, new TimeOnly(14, 15), kid: Attendance.Unknown, lessonNumber: 8);
        await AddLessonAsync(w, day, new TimeOnly(7, 30));

        await RunAsync(day, new TimeOnly(5, 31));  // «за 2 часа» к 7:30 — это 5:30: не шлём
        await RunAsync(day, new TimeOnly(7, 16));  // за 15 минут к 7:30
        await RunAsync(day, new TimeOnly(12, 16)); // за 2 часа к 14:15
        await RunAsync(day, new TimeOnly(14, 1));  // за 15 минут к 14:15
        await RunAsync(day, new TimeOnly(14, 20)); // занятие уже идёт — не напоминаем, даже если 14:00 пропустили

        var texts = await TextsAsync(w.TeacherId);
        Assert.Equal(3, texts.Count);
        Assert.StartsWith("⏰ Через 15 мин занятие: 7:30–8:10", texts[0]);
        Assert.EndsWith("Пока никто не записан.", texts[0]);
        Assert.StartsWith("⏰ Через 2 ч занятие: 8 урок (14:15–14:55)", texts[1]);
        Assert.EndsWith("Записаны (1 из 5): Ученик2088 Т.", texts[1]);
        Assert.StartsWith("⏰ Через 15 мин", texts[2]);
    }

    [DatabaseFact]
    public async Task Неотмеченные__в_19_00_число_и_ссылка()
    {
        var w = await CreateWorldAsync(2089);
        await AddLessonAsync(w, w.Monday, new TimeOnly(14, 15));
        await AddLessonAsync(w, w.Monday.AddDays(1), new TimeOnly(14, 15));
        await AddLessonAsync(w, w.Monday.AddDays(1), new TimeOnly(15, 15), LessonStatus.Held);
        await AddLessonAsync(w, w.Monday.AddDays(1), new TimeOnly(18, 50)); // ещё идёт в 19:05

        await RunAsync(w.Monday.AddDays(1), new TimeOnly(18, 59));
        Assert.Empty(await TextsAsync(w.TeacherId));

        await RunAsync(w.Monday.AddDays(1), new TimeOnly(19, 5));
        Assert.Equal("📝 Не отмечено занятий: 2. Отметьте, проведены ли они и кто был: https://test.example/teacher",
            Assert.Single(await TextsAsync(w.TeacherId)));
    }

    // ---------------------------------------------------------------- Кнопки

    private sealed class FixedTime(DateTime utc) : TimeProvider
    {
        public override DateTimeOffset GetUtcNow() => utc;
    }

    private sealed class FakeBot : IMaxBotClient
    {
        public List<MaxMessage> Answers { get; } = [];
        public Task<MaxBotInfo> GetMeAsync(CancellationToken ct = default) => throw new NotSupportedException();
        public Task<MaxUpdatesPage> GetUpdatesAsync(long? marker, int timeoutSeconds, CancellationToken ct = default) => throw new NotSupportedException();
        public Task SendMessageAsync(long userId, MaxMessage message, CancellationToken ct = default) => throw new NotSupportedException();

        public Task AnswerCallbackAsync(string callbackId, MaxMessage message, CancellationToken ct = default)
        {
            Answers.Add(message);
            return Task.CompletedTask;
        }
    }

    private async Task<Notification> LastNotificationAsync(int personId)
    {
        await using var db = database.CreateContext();
        return await db.Notifications.Where(n => n.RecipientId == personId).OrderBy(n => n.Id).LastAsync();
    }

    /// <summary>Нажатие кнопки под сообщением — так, как его присылает MAX: вместе с самим сообщением.</summary>
    private async Task<MaxMessage> PressAsync(long maxUserId, Notification notification, string payload, DateTime nowUtc)
    {
        var bot = new FakeBot();
        await using var db = database.CreateContext();
        var time = new FixedTime(nowUtc);
        var handler = new BotUpdateHandler(db, new PersonRegistration(db, time), bot, Options.Create(new MaxOptions { BotToken = "test" }),
            time, NullLogger<BotUpdateHandler>.Instance);
        var shown = LessonButtons.ToMessage(notification.Text, NotificationButton.FromJson(notification.Buttons)).ToJson();
        await handler.HandleAsync(new JsonObject
        {
            ["update_type"] = "message_callback",
            ["callback"] = new JsonObject
            {
                ["callback_id"] = Guid.NewGuid().ToString(), ["payload"] = payload, ["user"] = new JsonObject { ["user_id"] = maxUserId },
            },
            ["message"] = new JsonObject { ["body"] = shown },
        });
        return Assert.Single(bot.Answers);
    }

    private async Task<long> MaxIdAsync(int personId)
    {
        await using var db = database.CreateContext();
        return await db.People.Where(p => p.Id == personId).Select(p => p.MaxUserId!.Value).SingleAsync();
    }

    [DatabaseFact]
    public async Task Кнопка_не_смогу_прийти__отказ_только_от_этого_занятия()
    {
        var w = await CreateWorldAsync(2091);
        var wednesday = w.Monday.AddDays(2);
        await AddLessonAsync(w, wednesday, new TimeOnly(14, 15), kid: Attendance.Unknown, lessonNumber: 8);
        await AddLessonAsync(w, wednesday, new TimeOnly(15, 15), kid: Attendance.Unknown);
        await RunAsync(wednesday.AddDays(-1), new TimeOnly(17, 5));
        var reminder = await LastNotificationAsync(w.KidId);
        var buttons = NotificationButton.FromJson(reminder.Buttons);
        Assert.Equal(["🚫 Не смогу: 14:15 Предмет 2091", "🚫 Не смогу: 15:15 Предмет 2091"], buttons.Select(b => b.Text));
        Assert.Equal("🚫 Тест не сможет: 14:15 Предмет 2091",
            NotificationButton.FromJson((await LastNotificationAsync(w.ParentAllId)).Buttons)[0].Text);
        var evening = SchoolTime.ToUtc(wednesday.AddDays(-1), new TimeOnly(17, 10));

        // Чужой человек (учитель) нажать за ученика не может.
        var stranger = await PressAsync(await MaxIdAsync(w.TeacherId), reminder, buttons[0].Payload, evening);
        Assert.Contains("⚠️ Записывать ученика может он сам или его родитель.", stranger.Text);

        var answer = await PressAsync(await MaxIdAsync(w.KidId), reminder, buttons[0].Payload, evening);
        Assert.StartsWith(reminder.Text + "\n\n✔️ Отказ записан", answer.Text);
        Assert.Equal(buttons[1].Payload, Assert.Single(answer.Buttons!.SelectMany(r => r)).Payload); // вторая кнопка осталась

        await using var db = database.CreateContext();
        var statuses = await db.Enrollments.Where(e => e.StudentId == w.KidId).OrderBy(e => e.Lesson.StartsAt).Select(e => e.Status).ToListAsync();
        Assert.Equal([EnrollmentStatus.Declined, EnrollmentStatus.Active], statuses);
        Assert.StartsWith("➖ Отказ", (await TextsAsync(w.TeacherId)).Last());
    }

    [DatabaseFact]
    public async Task После_занятия__учителю_кнопки_проведено_и_не_состоялось()
    {
        var w = await CreateWorldAsync(2092);
        var day = w.Monday.AddDays(1);
        await AddLessonAsync(w, day, new TimeOnly(14, 15), kid: Attendance.Unknown, lessonNumber: 8);
        await AddLessonAsync(w, day, new TimeOnly(15, 15));

        await RunAsync(day, new TimeOnly(14, 56)); // первое закончилось в 14:55
        var ended = await LastNotificationAsync(w.TeacherId);
        Assert.StartsWith("🏁 Занятие закончилось: 8 урок (14:15–14:55)", ended.Text);
        Assert.Contains($"Записаны: Ученик2092 Т.", ended.Text);
        var buttons = NotificationButton.FromJson(ended.Buttons);
        Assert.Equal(["✅ Проведено", "❌ Не состоялось"], buttons.Select(b => b.Text));

        var teacherMax = await MaxIdAsync(w.TeacherId);
        var at = SchoolTime.ToUtc(day, new TimeOnly(15, 0));
        var held = await PressAsync(teacherMax, ended, buttons[0].Payload, at);
        Assert.Contains("✅ Отмечено: проведено", held.Text);
        Assert.Null(held.Buttons); // обе кнопки убраны
        // Второе нажатие (или отметка уже есть с сайта) ничего не перезаписывает.
        Assert.Contains("⚠️ Занятие уже отмечено", (await PressAsync(teacherMax, ended, buttons[1].Payload, at)).Text);

        await RunAsync(day, new TimeOnly(15, 56));
        var second = await LastNotificationAsync(w.TeacherId);
        Assert.Equal("Записанных не было.", second.Text.Split('\n')[1]);
        await PressAsync(teacherMax, second, NotificationButton.FromJson(second.Buttons)[1].Payload, SchoolTime.ToUtc(day, new TimeOnly(16, 0)));

        await using var db = database.CreateContext();
        var lessons = await db.Lessons.Include(l => l.Enrollments).Where(l => l.TeacherId == w.TeacherId).OrderBy(l => l.StartsAt).ToListAsync();
        Assert.Equal([LessonStatus.Held, LessonStatus.NotHeld], lessons.Select(l => l.Status));
        Assert.Equal(Attendance.Present, Assert.Single(lessons[0].Enrollments).Attendance);
    }

    [DatabaseFact]
    public async Task Сводка_родителю__в_воскресенье_прошлая_неделя_и_следующая()
    {
        var w = await CreateWorldAsync(2090);
        var sunday = w.Monday.AddDays(6);
        await AddLessonAsync(w, w.Monday.AddDays(2), new TimeOnly(14, 15), LessonStatus.Held, Attendance.Present);
        await AddLessonAsync(w, w.Monday.AddDays(3), new TimeOnly(14, 15), LessonStatus.Held, Attendance.Absent);
        await AddLessonAsync(w, sunday.AddDays(2), new TimeOnly(14, 15), kid: Attendance.Unknown, lessonNumber: 8);
        await AddLessonAsync(w, sunday.AddDays(9), new TimeOnly(14, 15), kid: Attendance.Unknown); // через неделю — не входит

        await RunAsync(sunday.AddDays(-1), new TimeOnly(18, 5)); // суббота — сводки нет
        Assert.Empty(await TextsAsync(w.ParentSummaryId));

        await RunAsync(sunday, new TimeOnly(18, 5));
        var summary = Assert.Single(await TextsAsync(w.ParentSummaryId));
        Assert.Contains("Ученик2090 Тест:\nПрошлая неделя: был(а) — 1, не был(а) — 1.\nНа этой неделе:\n• вт", summary);
        Assert.Single(summary.Split('\n'), line => line.StartsWith("• "));
        Assert.Single(await TextsAsync(w.ParentAllId)); // сводку получает и родитель со всеми уведомлениями
        Assert.Empty(await TextsAsync(w.KidId));
    }
}
