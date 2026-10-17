namespace Erudit.Domain;

/// <summary>
/// Перевод школьного (московского) времени в UTC и обратно.
/// Правило проекта: в базе время хранится в UTC, в местное переводим только для показа.
/// </summary>
public static class SchoolTime
{
    public static TimeZoneInfo Zone { get; } = TimeZoneInfo.FindSystemTimeZoneById("Europe/Moscow");

    /// <summary>Дата и время по школьным часам → момент в UTC.</summary>
    public static DateTime ToUtc(DateOnly date, TimeOnly time)
    {
        var local = date.ToDateTime(time, DateTimeKind.Unspecified);
        return TimeZoneInfo.ConvertTimeToUtc(local, Zone);
    }

    /// <summary>Момент в UTC → дата и время по школьным часам.</summary>
    public static DateTime ToSchool(DateTime utc) =>
        TimeZoneInfo.ConvertTimeFromUtc(DateTime.SpecifyKind(utc, DateTimeKind.Utc), Zone);
}
