using System.Globalization;
using Erudit.Domain.Lessons;

namespace Erudit.Domain.Notifications;

/// <summary>Что нужно знать о занятии для текста сообщения. Дата и время — по школьным часам.</summary>
public sealed record LessonInfo(
    string Subject,
    DateOnly Date,
    TimeOnly Start,
    int DurationMinutes,
    int? LessonNumber,
    string Room,
    string Teacher);

/// <summary>Ребёнок в недельной сводке: сколько был и пропустил на прошлой неделе, занятия на следующую.</summary>
public sealed record ChildWeek(string Name, int Attended, int Missed, IReadOnlyList<LessonInfo> Next);

/// <summary>Раздел ответа на команду бота: «Вы ведёте», «Вы записаны» или имя ребёнка — и строки занятий.</summary>
public sealed record ScheduleSection(string Title, IReadOnlyList<string> Lines);

/// <summary>Тексты сообщений бота — в одном месте, чтобы их было легко проверять и менять.</summary>
public static class NotificationText
{
    private static readonly CultureInfo Ru = CultureInfo.GetCultureInfo("ru-RU");

    /// <summary>«Математика, ср 12.11, 8 урок (14:15–14:55), Каб.1, учитель Соколова Е.В.»</summary>
    public static string Describe(LessonInfo l) =>
        $"{l.Subject}, {l.Date.ToString("ddd dd.MM", Ru)}, {Time(l)}, {l.Room}, учитель {l.Teacher}";

    /// <summary>Несколько занятий серии — одной строкой: «Математика, по средам с 12.11 по 25.12 (занятий: 7), …».</summary>
    public static string DescribeSeries(LessonInfo first, DateOnly lastDate, int count) => count <= 1
        ? Describe(first)
        : $"{first.Subject}, по {SeriesDay(first.Date.DayOfWeek)} с {first.Date:dd.MM} по {lastDate:dd.MM} (занятий: {count}), " +
          $"{Time(first)}, {first.Room}, учитель {first.Teacher}";

    /// <summary>«средам» — для «по средам».</summary>
    public static string SeriesDay(DayOfWeek day) => day switch
    {
        DayOfWeek.Monday => "понедельникам", DayOfWeek.Tuesday => "вторникам", DayOfWeek.Wednesday => "средам",
        DayOfWeek.Thursday => "четвергам", DayOfWeek.Friday => "пятницам", DayOfWeek.Saturday => "субботам", _ => "воскресеньям",
    };

    public static string Added(string lessons) => $"✅ Новая запись на занятие: {lessons}.";

    public static string Removed(string lessons) => $"🚫 Запись снята: {lessons}.";

    public static string Changed(string before, string after) => $"✏️ Изменение занятия.\nБыло: {before}.\nСтало: {after}.";

    public static string Cancelled(string lessons, string? reason) =>
        $"❌ Отмена занятия: {lessons}." + (string.IsNullOrWhiteSpace(reason) ? "" : $"\nПричина: {reason}.");

    public static string Restored(string lessons) => $"↩️ Занятие снова в расписании: {lessons}.";

    /// <summary>Учителю: занятие появилось не по его действию (создал завуч).</summary>
    public static string TeacherLessonAdded(string lessons) => $"📅 Вам добавлено занятие: {lessons}.";

    public static string TeacherSignedUp(string student, string lessons) => $"➕ Запись: {student}. {lessons}.";

    public static string TeacherDeclined(string student, string lessons) => $"➖ Отказ: {student}. {lessons}.";

    // ---------------------------------------------------------------- Напоминания и сводки (по времени)

    /// <summary>Строка списка: «8 урок (14:15–14:55) — Математика, Каб.1, учитель Соколова Е.В.»; с датой — «ср 12.11, 8 урок …».</summary>
    public static string LessonLine(LessonInfo l, bool withDate = false, bool withTeacher = true) =>
        (withDate ? $"{l.Date.ToString("ddd dd.MM", Ru)}, " : "") + $"{Time(l)} — {l.Subject}, {l.Room}" +
        (withTeacher ? $", учитель {l.Teacher}" : "");

    /// <summary>Ученику (родителю) накануне или утром: все занятия дня одним сообщением.</summary>
    public static string StudentDay(DateOnly date, bool tomorrow, IEnumerable<string> lines) =>
        $"🔔 {(tomorrow ? "Завтра" : "Сегодня")}, {date.ToString("ddd dd.MM", Ru)}, дополнительные занятия:\n" +
        string.Join("\n", lines.Select(line => $"• {line}"));

    /// <param name="students">Кто записан; пусто — «пока никто не записан».</param>
    public static string TeacherSoon(int minutesBefore, LessonInfo l, IReadOnlyList<string> students, int capacity) =>
        $"⏰ Через {(minutesBefore >= 60 ? $"{minutesBefore / 60} ч" : $"{minutesBefore} мин")} занятие: " +
        $"{LessonLine(l, withTeacher: false)}.\n" +
        (students.Count == 0 ? "Пока никто не записан." : $"Записаны ({students.Count} из {capacity}): {string.Join(", ", students).TrimEnd('.')}.");

    /// <summary>Учителю сразу после занятия — с кнопками «Проведено» / «Не состоялось».</summary>
    public static string LessonEnded(LessonInfo l, IReadOnlyList<string> students) =>
        $"🏁 Занятие закончилось: {LessonLine(l, withTeacher: false)}.\n" +
        (students.Count == 0 ? "Записанных не было." : $"Записаны: {string.Join(", ", students).TrimEnd('.')}.") +
        "\n«Проведено» — все записанные были; кого не было, отметьте на сайте.";

    public static string Unmarked(int count, string? link) =>
        $"📝 Не отмечено занятий: {count}. Отметьте, проведены ли они и кто был" + (link is null ? " — на сайте." : $": {link}");

    /// <summary>Родителю в воскресенье: по каждому ребёнку — итоги прошлой недели и занятия на следующую.</summary>
    public static string WeeklySummary(IEnumerable<ChildWeek> children) =>
        "📋 Сводка по дополнительным занятиям." + string.Concat(children.Select(c =>
            $"\n\n{c.Name}:" +
            (c.Attended + c.Missed > 0 ? $"\nПрошлая неделя: был(а) — {c.Attended}, не был(а) — {c.Missed}." : "") +
            (c.Next.Count == 0
                ? "\nНа этой неделе занятий нет."
                : "\nНа этой неделе:" + string.Concat(c.Next.Select(l => $"\n• {LessonLine(l, withDate: true)}")))));

    // ---------------------------------------------------------------- Команды бота «Сегодня» / «На неделю»

    public static string TodayTitle(DateOnly date) => $"📅 Сегодня, {date.ToString("ddd dd.MM", Ru)}";

    public static string WeekTitle(DateOnly from, DateOnly to) =>
        $"🗓 Занятия с {from.ToString("ddd dd.MM", Ru)} по {to.ToString("ddd dd.MM", Ru)}";

    /// <summary>Пометка перед строкой: проведено, не состоялось, отменено; у запланированного — ничего.</summary>
    public static string StatusMark(LessonStatus status) => status switch
    {
        LessonStatus.Held => "✅ ",
        LessonStatus.NotHeld => "⚪ не состоялось: ",
        LessonStatus.Cancelled => "❌ отменено: ",
        _ => "",
    };

    /// <summary>Ответ на команду: заголовок и разделы («Вы ведёте», «Вы записаны», дети); нет ни одного — «занятий нет».</summary>
    public static string Schedule(string title, IReadOnlyList<ScheduleSection> sections) => sections.Count == 0
        ? $"{title}: дополнительных занятий нет."
        : title + string.Concat(sections.Select(s => $"\n\n{s.Title}:" + string.Concat(s.Lines.Select(line => $"\n• {line}"))));

    /// <summary>Родителю: чьё это занятие. «Иванов Пётр: ✅ Новая запись…»</summary>
    public static string ForParent(IEnumerable<string> children, string text) => $"{string.Join(", ", children)}: {text}";

    private static string Time(LessonInfo l)
    {
        var range = $"{l.Start:H:mm}–{l.Start.AddMinutes(l.DurationMinutes):H:mm}";
        return l.LessonNumber is { } number ? $"{number} урок ({range})" : range;
    }
}
