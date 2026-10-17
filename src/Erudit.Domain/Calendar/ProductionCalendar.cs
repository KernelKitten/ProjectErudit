namespace Erudit.Domain.Calendar;

/// <summary>
/// Превращает производственный календарь (списки нерабочих дат) в наши исключения CalendarDay.
///
/// В файле calendar.json массив holidays содержит ВСЕ нерабочие дни: и обычные выходные, и праздники.
/// Мы храним только отличия от обычной недели «Пн–Пт рабочие»:
///   праздник или нерабочий день в будни → CalendarDay { IsWorkingDay = false };
///   рабочая суббота или воскресенье (перенос) → CalendarDay { IsWorkingDay = true }.
/// </summary>
public static class ProductionCalendar
{
    /// <summary>Исключения за все годы файла, начиная с <paramref name="fromYear"/>.</summary>
    /// <param name="holidays">Нерабочие дни (выходные + праздники).</param>
    /// <param name="nowork">Дополнительные нерабочие дни по указам.</param>
    public static ProductionCalendarResult ToExceptions(IEnumerable<DateOnly> holidays, IEnumerable<DateOnly> nowork, int fromYear)
    {
        var nonWorking = holidays.Concat(nowork).ToHashSet();

        // Какие годы описаны в файле: те, для которых в нём есть хоть одна дата.
        var years = nonWorking.Select(d => d.Year).Where(y => y >= fromYear).Distinct().Order().ToList();

        var days = new List<CalendarDay>();
        foreach (var year in years)
        {
            for (var date = new DateOnly(year, 1, 1); date.Year == year; date = date.AddDays(1))
            {
                var isWeekend = date.DayOfWeek is DayOfWeek.Saturday or DayOfWeek.Sunday;
                var isWorking = !nonWorking.Contains(date);
                if (isWorking == isWeekend) // рабочий выходной или нерабочий будний — это исключение
                {
                    days.Add(new CalendarDay { Date = date, IsWorkingDay = isWorking });
                }
            }
        }
        return new ProductionCalendarResult(years, days);
    }
}

/// <summary>Годы, которые описывает файл, и исключения в них.</summary>
public sealed record ProductionCalendarResult(IReadOnlyList<int> Years, IReadOnlyList<CalendarDay> Exceptions);
