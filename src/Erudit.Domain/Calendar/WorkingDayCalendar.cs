namespace Erudit.Domain.Calendar;

/// <summary>
/// Отвечает на вопрос «рабочий ли это день». Создаётся из записей CalendarDay,
/// дальше работает только в памяти — поэтому его легко проверять тестами.
/// </summary>
public sealed class WorkingDayCalendar
{
    private readonly Dictionary<DateOnly, bool> _exceptions;

    public WorkingDayCalendar(IEnumerable<CalendarDay> days)
    {
        _exceptions = days.ToDictionary(d => d.Date, d => d.IsWorkingDay);
    }

    /// <summary>Календарь без исключений: рабочие только понедельник — пятница.</summary>
    public static WorkingDayCalendar Empty { get; } = new([]);

    public bool IsWorkingDay(DateOnly date)
    {
        if (_exceptions.TryGetValue(date, out var isWorking))
        {
            return isWorking;
        }
        return date.DayOfWeek is not (DayOfWeek.Saturday or DayOfWeek.Sunday);
    }
}
