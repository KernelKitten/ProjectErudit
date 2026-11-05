using System.Text.Json.Nodes;
using Erudit.Domain;
using Erudit.Domain.Lessons;
using Erudit.Domain.Notifications;
using Erudit.Domain.People;
using Erudit.Infrastructure.Lessons;
using Erudit.Infrastructure.Max;
using Erudit.Infrastructure.Persistence;
using Microsoft.EntityFrameworkCore;

namespace Erudit.Infrastructure.Notifications;

/// <summary>
/// Кнопки в сообщениях о занятиях (этап 6, шаг 4): «Не смогу прийти» в напоминаниях ученику и родителю,
/// «Проведено» / «Не состоялось» учителю после занятия. Права и правила проверяют те же сервисы, что и на сайте.
/// </summary>
public static class LessonButtons
{
    private const string Skip = "skip:";       // skip:{занятие}:{ученик}
    private const string Held = "held:";       // held:{занятие}
    private const string NotHeld = "notheld:"; // notheld:{занятие}

    public static bool IsLessonPayload(string payload) =>
        payload.StartsWith(Skip) || payload.StartsWith(Held) || payload.StartsWith(NotHeld);

    /// <summary>«🚫 Не смогу: 14:15 Математика»; родителю (<paramref name="childFirstName"/> задано) — «🚫 Пётр не сможет: …».</summary>
    public static NotificationButton SkipButton(int lessonId, int studentId, string? childFirstName, DateTime startsAtUtc, string subject)
    {
        // Надпись на кнопке должна быть короткой: длинное название предмета обрезаем.
        var when = $"{SchoolTime.ToSchool(startsAtUtc):H:mm} {(subject.Length <= 18 ? subject : subject[..17] + "…")}";
        var text = childFirstName is null ? $"🚫 Не смогу: {when}" : $"🚫 {childFirstName} не сможет: {when}";
        return new(text, $"{Skip}{lessonId}:{studentId}");
    }

    public static IReadOnlyList<NotificationButton> MarkButtons(int lessonId) =>
        [new("✅ Проведено", $"{Held}{lessonId}"), new("❌ Не состоялось", $"{NotHeld}{lessonId}")];

    /// <summary>Сообщение для MAX: кнопки — по одной в ряд.</summary>
    public static MaxMessage ToMessage(string text, IReadOnlyList<NotificationButton> buttons) =>
        new(text, buttons.Count == 0 ? null : buttons.Select(b => (IReadOnlyList<MaxButton>)[MaxButton.Callback(b.Text, b.Payload)]).ToList());

    /// <summary>
    /// Нажатие кнопки. Возвращает, чем заменить сообщение: прежний текст + итог нажатия; нажатая кнопка
    /// (а после отметки — обе кнопки учителя) пропадает, остальные остаются.
    /// </summary>
    public static async Task<MaxMessage> HandleAsync(EruditDbContext db, TimeProvider time, Person person, string payload,
        JsonObject? message, CancellationToken ct)
    {
        string result;
        try
        {
            result = await ApplyAsync(db, time, person, payload, ct);
        }
        catch (InvalidOperationException ex)
        {
            // Правило не позволяет (занятие началось, уже отмечено, чужое) — объясняем, кнопку убираем.
            db.ChangeTracker.Clear();
            result = $"⚠️ {ex.Message}";
        }

        var (text, buttons) = Original(message);
        string[] gone = payload.StartsWith(Skip)
            ? [payload]
            : [.. MarkButtons(int.TryParse(payload.Split(':')[1], out var id) ? id : 0).Select(b => b.Payload)];
        var left = buttons.Where(b => !gone.Contains(b.Payload)).ToList();
        return ToMessage(text is null ? result : $"{text}\n\n{result}", left);
    }

    private static async Task<string> ApplyAsync(EruditDbContext db, TimeProvider time, Person person, string payload, CancellationToken ct)
    {
        db.CurrentPersonId = person.Id; // в журнале изменений автор — нажавший кнопку
        var parts = payload.Split(':');
        if (!int.TryParse(parts[1], out var lessonId))
        {
            throw new InvalidOperationException("Кнопка устарела.");
        }
        var lesson = await db.Lessons.AsNoTracking()
            .Where(l => l.Id == lessonId)
            .Select(l => new { l.Status, l.StartsAt, l.DurationMinutes, Subject = l.Subject.Name })
            .SingleOrDefaultAsync(ct)
            ?? throw new InvalidOperationException("Занятие не найдено.");
        var what = $"{SchoolTime.ToSchool(lesson.StartsAt):dd.MM H:mm}, {lesson.Subject}";

        if (parts[0] + ":" == Skip)
        {
            if (parts.Length < 3 || !int.TryParse(parts[2], out var studentId))
            {
                throw new InvalidOperationException("Кнопка устарела.");
            }
            await new SignupService(db, time).DeclineAsync(lessonId, studentId, EditScope.OnlyThis, person.Id, ct);
            return $"✔️ Отказ записан ({what}). Учитель получит сообщение.";
        }

        // Отметку на сайте (с отсутствующими) кнопка из бота не перезапишет.
        if (lesson.Status != LessonStatus.Planned)
        {
            throw new InvalidOperationException("Занятие уже отмечено или отменено. Исправить можно на сайте.");
        }
        var service = new LessonService(db, time);
        var actor = new LessonActor(person.Id, IsManager: false);
        if (parts[0] + ":" == Held)
        {
            await service.MarkHeldAsync(lessonId, [], lesson.DurationMinutes, actor, ct);
            return $"✅ Отмечено: проведено ({what}), были все записанные. Кого не было — исправьте на сайте.";
        }
        await service.MarkNotHeldAsync(lessonId, actor, ct);
        return $"❌ Отмечено: не состоялось ({what}).";
    }

    /// <summary>Текст и кнопки сообщения, под которым нажали кнопку (MAX присылает его вместе с нажатием).</summary>
    private static (string? Text, List<NotificationButton> Buttons) Original(JsonObject? message)
    {
        var body = message?["body"];
        var text = body?["text"]?.GetValue<string>();
        var buttons = new List<NotificationButton>();
        foreach (var attachment in body?["attachments"]?.AsArray() ?? [])
        {
            if (attachment?["type"]?.GetValue<string>() != "inline_keyboard")
            {
                continue;
            }
            foreach (var row in attachment["payload"]?["buttons"]?.AsArray() ?? [])
            {
                foreach (var b in row?.AsArray() ?? [])
                {
                    if (b?["payload"]?.GetValue<string>() is { } p && b["text"]?.GetValue<string>() is { } t)
                    {
                        buttons.Add(new NotificationButton(t, p));
                    }
                }
            }
        }
        return (text, buttons);
    }
}
