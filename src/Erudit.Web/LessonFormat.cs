using Erudit.Domain;
using Erudit.Domain.Lessons;
using MudBlazor;

namespace Erudit.Web;

/// <summary>Как показывать занятия на страницах: даты по школьному времени, статусы по-русски.</summary>
public static class LessonFormat
{
    /// <summary>«ср 21.10»</summary>
    public static string Date(DateTime startsAtUtc) => SchoolTime.ToSchool(startsAtUtc).ToString("ddd dd.MM");

    /// <summary>«8 урок, 14:15–14:55» или «14:30–15:30», если время задано вручную.</summary>
    public static string Time(Lesson lesson)
    {
        var start = SchoolTime.ToSchool(lesson.StartsAt);
        var range = $"{start:HH:mm}–{start.AddMinutes(lesson.DurationMinutes):HH:mm}";
        return lesson.LessonNumber is { } n ? $"{n} урок, {range}" : range;
    }

    public static string Status(LessonStatus status) => status switch
    {
        LessonStatus.Planned => "Запланировано",
        LessonStatus.Held => "Проведено",
        LessonStatus.NotHeld => "Не состоялось",
        LessonStatus.Cancelled => "Отменено",
        _ => status.ToString(),
    };

    public static Color StatusColor(LessonStatus status) => status switch
    {
        LessonStatus.Held => Color.Success,
        LessonStatus.NotHeld => Color.Warning,
        LessonStatus.Cancelled => Color.Error,
        _ => Color.Default,
    };

    /// <summary>Начало текущей недели (понедельник 00:00 по школьному времени) в UTC.</summary>
    public static DateTime WeekStartUtc(DateTime nowUtc)
    {
        var today = DateOnly.FromDateTime(SchoolTime.ToSchool(nowUtc));
        var monday = today.AddDays(-(((int)today.DayOfWeek + 6) % 7));
        return SchoolTime.ToUtc(monday, TimeOnly.MinValue);
    }
}
