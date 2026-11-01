using System.Globalization;

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

    /// <summary>Родителю: чьё это занятие. «Иванов Пётр: ✅ Новая запись…»</summary>
    public static string ForParent(IEnumerable<string> children, string text) => $"{string.Join(", ", children)}: {text}";

    private static string Time(LessonInfo l)
    {
        var range = $"{l.Start:H:mm}–{l.Start.AddMinutes(l.DurationMinutes):H:mm}";
        return l.LessonNumber is { } number ? $"{number} урок ({range})" : range;
    }
}
