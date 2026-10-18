namespace Erudit.Web.Components.Pages.Calendar;

/// <summary>Один день в сетке календаря — всё, что нужно, чтобы его нарисовать.</summary>
public sealed record CalendarCell(
    DateOnly Date,
    bool IsWorking,
    bool IsManual,
    bool IsToday,
    bool VacationAll,  // каникулы у всей школы
    bool VacationPart, // каникулы только у части классов
    string? Hint);     // всплывающая подсказка: названия каникул
