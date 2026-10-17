namespace Erudit.Domain.Calendar;

/// <summary>
/// День из производственного календаря, у которого статус отличается от обычного
/// (праздник в будни, рабочая суббота) или исправлен администратором.
/// Дни, которых в таблице нет, считаются рабочими с понедельника по пятницу.
/// </summary>
public class CalendarDay
{
    /// <summary>Дата — она же ключ таблицы.</summary>
    public DateOnly Date { get; set; }

    public bool IsWorkingDay { get; set; }

    /// <summary>Исправлено администратором вручную — автоматическая загрузка такой день не трогает.</summary>
    public bool IsManual { get; set; }
}
