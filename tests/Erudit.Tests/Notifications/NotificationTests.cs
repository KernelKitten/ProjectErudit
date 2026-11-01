using Erudit.Domain;
using Erudit.Domain.Lessons;
using Erudit.Domain.Notifications;
using Erudit.Domain.People;
using Erudit.Domain.School;
using Erudit.Infrastructure.Lessons;
using Erudit.Infrastructure.Max;
using Erudit.Infrastructure.Notifications;
using Erudit.Infrastructure.Persistence;
using Erudit.Tests.Database;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging.Abstractions;

namespace Erudit.Tests.Notifications;

/// <summary>
/// Очередь сообщений: кто что получает при записи, отказе, изменении и отмене; отправка и повторы.
/// У каждого теста свой учебный год (2079–2086) и свои люди; «сейчас» — понедельник, 9:00, за неделю до первого занятия.
/// </summary>
[Collection(DatabaseCollection.Name)]
public class NotificationTests(TestDatabase database)
{
    private sealed class FixedTime(DateTime utc) : TimeProvider
    {
        public override DateTimeOffset GetUtcNow() => utc;
    }

    private sealed class FakeBot : IMaxBotClient
    {
        public List<(long UserId, string Text)> Sent { get; } = [];
        public int FailuresLeft { get; set; }

        public Task<MaxBotInfo> GetMeAsync(CancellationToken ct = default) => throw new NotSupportedException();
        public Task<MaxUpdatesPage> GetUpdatesAsync(long? marker, int timeoutSeconds, CancellationToken ct = default) => throw new NotSupportedException();
        public Task AnswerCallbackAsync(string callbackId, MaxMessage message, CancellationToken ct = default) => throw new NotSupportedException();

        public Task SendMessageAsync(long userId, MaxMessage message, CancellationToken ct = default)
        {
            if (FailuresLeft > 0)
            {
                FailuresLeft--;
                throw new HttpRequestException("MAX недоступен");
            }
            Sent.Add((userId, message.Text));
            return Task.CompletedTask;
        }
    }

    private sealed record World(int Year, int TeacherId, int SubjectId, int RoomId, int RoomBId, int ManagerId,
        int KidId, int OtherKidId, int NoMaxKidId, int ParentAllId, int ParentSummaryId, DateOnly Monday, TimeProvider Now);

    /// <summary>Учитель, завуч, трое учеников (у третьего нет MAX) и родители первого: со всеми уведомлениями и только со сводкой.</summary>
    private async Task<World> CreateWorldAsync(int year)
    {
        var academicYear = await database.CreateSandboxYearAsync(year);
        await using var db = database.CreateContext();
        var next = year * 1000L;
        Person New(string last, Role role, bool max = true) => new()
        {
            LastName = last, FirstName = "Тест", MaxUserId = max ? ++next : null, Roles = [new PersonRole { Role = role }],
        };
        var teacher = New($"Учитель{year}", Role.Teacher);
        var manager = New($"Завуч{year}", Role.HeadTeacher);
        var kid = New($"Ученик{year}", Role.Student);
        var other = New($"Другой{year}", Role.Student);
        var noMax = New($"БезМакса{year}", Role.Student, max: false);
        var parentAll = New($"РодительВсе{year}", Role.Parent);
        var parentSummary = New($"РодительСводка{year}", Role.Parent);
        var subject = new Subject { Name = $"Предмет {year}" };
        var (room, roomB) = (new Room { Name = $"Каб.{year}" }, new Room { Name = $"Каб.{year}б" });
        var schoolClass = new SchoolClass { AcademicYearId = academicYear.Id, Grade = 7, Letter = "а" };
        db.AddRange(teacher, manager, kid, other, noMax, parentAll, parentSummary, subject, room, roomB, schoolClass);
        db.Add(new ClassMembership { Student = kid, SchoolClass = schoolClass, FromDate = academicYear.StartDate });
        db.Add(new ParentLink { Parent = parentAll, Student = kid, NotifyAll = true });
        db.Add(new ParentLink { Parent = parentSummary, Student = kid });
        await db.SaveChangesAsync();

        var first = new DateOnly(year, 9, 1);
        var monday = first.AddDays(((int)DayOfWeek.Monday - (int)first.DayOfWeek + 7) % 7);
        return new World(year, teacher.Id, subject.Id, room.Id, roomB.Id, manager.Id, kid.Id, other.Id, noMax.Id, parentAll.Id, parentSummary.Id,
            monday, new FixedTime(SchoolTime.ToUtc(monday, new TimeOnly(9, 0))));
    }

    private static LessonDraft Draft(World w, DateOnly date, params int[] students) => new()
    {
        TeacherId = w.TeacherId, SubjectId = w.SubjectId, RoomId = w.RoomId, Date = date, LessonNumber = 8,
        StartTime = new TimeOnly(14, 15), DurationMinutes = 40, Capacity = 5, StudentIds = students,
    };

    private static LessonDraft Series(World w, params int[] students) => Draft(w, w.Monday.AddDays(7), students) with
    {
        RepeatDays = [DayOfWeek.Monday], RepeatUntil = w.Monday.AddDays(28),
    };

    private async Task<int> CreateAsync(World w, LessonDraft draft)
    {
        await using var db = database.CreateContext();
        await new LessonService(db, w.Now).CreateAsync(draft, new LessonActor(w.TeacherId, false), warningsConfirmed: true);
        return await db.Lessons.Where(l => l.TeacherId == w.TeacherId).OrderBy(l => l.StartsAt).Select(l => l.Id).FirstAsync();
    }

    private async Task<List<string>> TextsAsync(int personId)
    {
        await using var db = database.CreateContext();
        return await db.Notifications.Where(n => n.RecipientId == personId).OrderBy(n => n.Id).Select(n => n.Text).ToListAsync();
    }

    private async Task ClearAsync()
    {
        await using var db = database.CreateContext();
        await db.Notifications.ExecuteDeleteAsync();
    }

    [Fact]
    public void Текст__занятие_и_серия()
    {
        var info = new LessonInfo("Математика", new DateOnly(2026, 11, 11), new TimeOnly(14, 15), 40, 8, "Каб.1", "Соколова Е.В.");

        Assert.Equal("Математика, ср 11.11, 8 урок (14:15–14:55), Каб.1, учитель Соколова Е.В.", NotificationText.Describe(info));
        Assert.Equal("Математика, по средам с 11.11 по 23.12 (занятий: 7), 8 урок (14:15–14:55), Каб.1, учитель Соколова Е.В.",
            NotificationText.DescribeSeries(info, new DateOnly(2026, 12, 23), 7));
        Assert.Equal(NotificationText.Describe(info), NotificationText.DescribeSeries(info, info.Date, 1));
        Assert.Contains("14:15–14:55, Каб.1", NotificationText.Describe(info with { LessonNumber = null }));
    }

    [DatabaseFact]
    public async Task Ученик_записался__учителю_и_родителю_со_всеми_уведомлениями__себе_нет()
    {
        var w = await CreateWorldAsync(2079);
        var lesson = await CreateAsync(w, Draft(w, w.Monday.AddDays(7)));

        await using var db = database.CreateContext();
        await new SignupService(db, w.Now).SignUpAsync(lesson, w.KidId, EditScope.OnlyThis, w.KidId);

        var teacher = Assert.Single(await TextsAsync(w.TeacherId));
        Assert.StartsWith($"➕ Запись: Ученик{w.Year} Тест (7а).", teacher);
        Assert.StartsWith($"Ученик{w.Year} Тест: ✅ Новая запись", Assert.Single(await TextsAsync(w.ParentAllId)));
        Assert.Empty(await TextsAsync(w.KidId));
        Assert.Empty(await TextsAsync(w.ParentSummaryId));
    }

    [DatabaseFact]
    public async Task Родитель_записал_и_отказался_за_серию__ученику_и_учителю_по_одному_сообщению()
    {
        var w = await CreateWorldAsync(2080);
        var lesson = await CreateAsync(w, Series(w));

        await using var db = database.CreateContext();
        var service = new SignupService(db, w.Now);
        await service.SignUpAsync(lesson, w.KidId, EditScope.ThisAndFollowing, w.ParentAllId);
        await service.DeclineAsync(lesson, w.KidId, EditScope.ThisAndFollowing, w.ParentAllId);

        var kid = await TextsAsync(w.KidId);
        Assert.Equal(2, kid.Count);
        Assert.Contains("занятий: 4", kid[0]);
        Assert.StartsWith("🚫 Запись снята", kid[1]);
        var teacher = await TextsAsync(w.TeacherId);
        Assert.Equal(2, teacher.Count);
        Assert.StartsWith("➖ Отказ", teacher[1]);
        Assert.Empty(await TextsAsync(w.ParentAllId)); // действовал сам
    }

    [DatabaseFact]
    public async Task Учитель_создал_с_учениками__записанным_одно_сообщение__без_MAX_ничего()
    {
        var w = await CreateWorldAsync(2081);
        await CreateAsync(w, Series(w, w.KidId, w.NoMaxKidId));

        Assert.Contains("занятий: 4", Assert.Single(await TextsAsync(w.KidId)));
        Assert.Empty(await TextsAsync(w.NoMaxKidId));
        Assert.Empty(await TextsAsync(w.TeacherId)); // создал сам
    }

    [DatabaseFact]
    public async Task Изменение__важное_состав_и_тема()
    {
        var w = await CreateWorldAsync(2082);
        var lesson = await CreateAsync(w, Draft(w, w.Monday.AddDays(7), w.KidId));
        await ClearAsync();
        var actor = new LessonActor(w.TeacherId, false);

        await using (var db = database.CreateContext())
        {
            // Только тема — ученика не беспокоим.
            await new LessonService(db, w.Now).UpdateAsync(lesson, Draft(w, w.Monday.AddDays(7), w.KidId) with { Topic = "Дроби" },
                EditScope.OnlyThis, actor, warningsConfirmed: true);
        }
        Assert.Empty(await TextsAsync(w.KidId));

        await using (var db = database.CreateContext())
        {
            // Другой кабинет и другой состав; менял завуч — учитель тоже узнаёт.
            await new LessonService(db, w.Now).UpdateAsync(lesson, Draft(w, w.Monday.AddDays(7), w.KidId, w.OtherKidId) with { RoomId = w.RoomBId },
                EditScope.OnlyThis, new LessonActor(w.ManagerId, true), warningsConfirmed: true);
        }
        var kid = Assert.Single(await TextsAsync(w.KidId));
        Assert.Contains("Было:", kid);
        Assert.Contains($"Стало: Предмет {w.Year}", kid);
        Assert.StartsWith("✅ Новая запись", Assert.Single(await TextsAsync(w.OtherKidId)));
        var teacher = Assert.Single(await TextsAsync(w.TeacherId));
        Assert.Contains($"Записаны: Другой{w.Year} Т.", teacher);
        Assert.Contains($"Изменения внёс(ла): Завуч{w.Year} Т.", teacher);
        Assert.Empty(await TextsAsync(w.ManagerId));

        await ClearAsync();
        await using (var db = database.CreateContext())
        {
            // Убрали ученика.
            await new LessonService(db, w.Now).UpdateAsync(lesson, Draft(w, w.Monday.AddDays(7), w.KidId) with { RoomId = w.RoomBId },
                EditScope.OnlyThis, actor, warningsConfirmed: true);
        }
        Assert.StartsWith("🚫 Запись снята", Assert.Single(await TextsAsync(w.OtherKidId)));
        Assert.Empty(await TextsAsync(w.KidId));
    }

    [DatabaseFact]
    public async Task Серия__перенос_отмена_и_возврат_одним_сообщением()
    {
        var w = await CreateWorldAsync(2083);
        var lesson = await CreateAsync(w, Series(w, w.KidId));
        await ClearAsync();
        var actor = new LessonActor(w.TeacherId, false);

        int moved;
        await using (var db = database.CreateContext())
        {
            // Со вторника: старые занятия отменяются, по новому дню создаются новые.
            await new LessonService(db, w.Now).UpdateAsync(lesson, Draft(w, w.Monday.AddDays(8), w.KidId), EditScope.ThisAndFollowing, actor, warningsConfirmed: true);
            moved = await db.Lessons.Where(l => l.TeacherId == w.TeacherId && l.Status == LessonStatus.Planned).OrderBy(l => l.StartsAt).Select(l => l.Id).FirstAsync();
        }
        var text = Assert.Single(await TextsAsync(w.KidId));
        Assert.Contains("по понедельникам", text);
        Assert.Contains("по вторникам", text);

        await ClearAsync();
        await using (var db = database.CreateContext())
        {
            await new LessonService(db, w.Now).CancelAsync(moved, EditScope.ThisAndFollowing, "болезнь", new LessonActor(w.ManagerId, true));
        }
        var cancelled = Assert.Single(await TextsAsync(w.KidId));
        Assert.StartsWith("❌ Отмена занятия", cancelled);
        Assert.Contains("Причина: болезнь.", cancelled);
        Assert.Contains($"Изменения внёс(ла): Завуч{w.Year} Т.", Assert.Single(await TextsAsync(w.TeacherId)));

        await ClearAsync();
        await using (var db = database.CreateContext())
        {
            await new LessonService(db, w.Now).RestoreAsync(moved, actor, warningsConfirmed: true);
        }
        Assert.StartsWith("↩️ Занятие снова в расписании", Assert.Single(await TextsAsync(w.KidId)));
    }

    [DatabaseFact]
    public async Task Прошедшие_занятия__сообщений_нет()
    {
        var w = await CreateWorldAsync(2084);
        var lesson = await CreateAsync(w, Draft(w, w.Monday.AddDays(7), w.KidId));
        await ClearAsync();

        // Занятие уже прошло, учитель отменяет его задним числом.
        var later = new FixedTime(SchoolTime.ToUtc(w.Monday.AddDays(8), new TimeOnly(9, 0)));
        await using var db = database.CreateContext();
        await new LessonService(db, later).CancelAsync(lesson, EditScope.OnlyThis, null, new LessonActor(w.TeacherId, false));

        Assert.Empty(await TextsAsync(w.KidId));
    }

    [DatabaseFact]
    public async Task Отправка__успех_устаревшее_и_не_уходит_второй_раз()
    {
        var w = await CreateWorldAsync(2085);
        await ClearAsync();
        await using var db = database.CreateContext();
        var now = w.Now.GetUtcNow().UtcDateTime;
        var maxId = await db.People.Where(p => p.Id == w.KidId).Select(p => p.MaxUserId!.Value).SingleAsync();
        db.AddRange(
            new Notification { RecipientId = w.KidId, Text = "свежее", CreatedAt = now, NextAttemptAt = now, ExpiresAt = now.AddDays(1) },
            new Notification { RecipientId = w.KidId, Text = "устарело", CreatedAt = now, NextAttemptAt = now, ExpiresAt = now.AddMinutes(-1) },
            new Notification { RecipientId = w.KidId, Text = "позже", CreatedAt = now, NextAttemptAt = now.AddMinutes(1), ExpiresAt = now.AddDays(1) },
            new Notification { RecipientId = w.NoMaxKidId, Text = "без MAX", CreatedAt = now, NextAttemptAt = now, ExpiresAt = now.AddDays(1) });
        await db.SaveChangesAsync();
        var bot = new FakeBot();
        var sender = new NotificationSender(db, bot, w.Now, NullLogger<NotificationSender>.Instance);

        Assert.Equal(1, await sender.SendDueAsync());
        Assert.Equal(0, await sender.SendDueAsync());

        Assert.Equal((maxId, "свежее"), Assert.Single(bot.Sent));
        var statuses = await db.Notifications.AsNoTracking().ToDictionaryAsync(n => n.Text, n => n.Status);
        Assert.Equal(NotificationStatus.Sent, statuses["свежее"]);
        Assert.Equal(NotificationStatus.Expired, statuses["устарело"]);
        Assert.Equal(NotificationStatus.Pending, statuses["позже"]);
        Assert.Equal(NotificationStatus.Failed, statuses["без MAX"]);
    }

    [DatabaseFact]
    public async Task Отправка__MAX_недоступен__повторы_и_после_пятой_неудачи_всё()
    {
        var w = await CreateWorldAsync(2086);
        await ClearAsync();
        await using var db = database.CreateContext();
        var now = w.Now.GetUtcNow().UtcDateTime;
        var notification = new Notification { RecipientId = w.KidId, Text = "важное", CreatedAt = now, NextAttemptAt = now, ExpiresAt = now.AddDays(7) };
        db.Add(notification);
        await db.SaveChangesAsync();
        var bot = new FakeBot { FailuresLeft = 5 };

        NotificationSender SenderAt(DateTime at) => new(db, bot, new FixedTime(at), NullLogger<NotificationSender>.Instance);

        await SenderAt(now).SendDueAsync();
        Assert.Equal((1, NotificationStatus.Pending, now.AddMinutes(1)), (notification.Attempts, notification.Status, notification.NextAttemptAt));
        Assert.Equal("MAX недоступен", notification.LastError);

        // Раньше назначенного времени повтора не отправляем.
        await SenderAt(now.AddSeconds(30)).SendDueAsync();
        Assert.Equal(1, notification.Attempts);

        for (var i = 2; i <= 5; i++)
        {
            await SenderAt(notification.NextAttemptAt).SendDueAsync();
            Assert.Equal(i, notification.Attempts);
        }
        Assert.Equal(NotificationStatus.Failed, notification.Status);

        // Пока оставались попытки, MAX мог и ожить: тогда сообщение уходит.
        var second = new Notification { RecipientId = w.KidId, Text = "второе", CreatedAt = now, NextAttemptAt = now, ExpiresAt = now.AddDays(7) };
        db.Add(second);
        await db.SaveChangesAsync();
        bot.FailuresLeft = 1;
        await SenderAt(now).SendDueAsync();
        await SenderAt(second.NextAttemptAt).SendDueAsync();
        Assert.Equal(NotificationStatus.Sent, second.Status);
        Assert.Equal("второе", Assert.Single(bot.Sent).Text);
    }
}
