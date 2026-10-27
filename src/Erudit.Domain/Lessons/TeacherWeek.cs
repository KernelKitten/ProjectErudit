using Erudit.Domain.Calendar;
using Erudit.Domain.School;

namespace Erudit.Domain.Lessons;

/// <summary>Занятие в сетке недели — только то, что нужно для показа. Время — по школьным часам.</summary>
/// <param name="SubjectShort">Короткое название предмета («Матем») — для узкой клетки сетки.</param>
public sealed record WeekLesson(
    int Id,
    DateTime Start,
    int DurationMinutes,
    int? LessonNumber,
    string Subject,
    string SubjectShort,
    string Room,
    int Enrolled,
    int Capacity,
    LessonStatus Status,
    bool IsSeries,
    string? Topic)
{
    public DateOnly Date => DateOnly.FromDateTime(Start);
    public TimeOnly StartTime => TimeOnly.FromDateTime(Start);
    public TimeOnly EndTime => StartTime.AddMinutes(DurationMinutes);
}

/// <summary>Строка сетки — один день.</summary>
/// <param name="VacationAll">Каникулы у всей школы — их название; иначе пусто.</param>
/// <param name="VacationPart">Каникулы только у части классов — «Доп. каникулы (1 кл.)»; иначе пусто.</param>
public sealed record WeekDay(DateOnly Date, bool IsWorking, bool IsToday, string? VacationAll, string? VacationPart);

/// <summary>
/// Неделя учителя в виде сетки, как школьное расписание: строки — дни, столбцы — уроки по звонкам.
/// Собирается в памяти из уже загруженных данных — поэтому легко проверяется тестами.
/// </summary>
public sealed class TeacherWeek
{
    /// <summary>Уроки, которые показываем всегда. 0, 8 и 9 бывают редко — их столбцы только по необходимости.</summary>
    public const int UsualFirst = 1;
    public const int UsualLast = 7;

    private readonly Dictionary<(DateOnly Date, int Period), List<WeekLesson>> _cells;

    private TeacherWeek(DateOnly monday, IReadOnlyList<WeekDay> days, IReadOnlyList<LessonPeriod> columns,
        IReadOnlyList<LessonPeriod> allPeriods, Dictionary<(DateOnly, int), List<WeekLesson>> cells)
    {
        Monday = monday;
        Days = days;
        Columns = columns;
        AllPeriods = allPeriods;
        _cells = cells;
    }

    public DateOnly Monday { get; }
    public IReadOnlyList<WeekDay> Days { get; }

    /// <summary>Столбцы, которые показываем: уроки 1–7 и редкие уроки, если на них есть занятия.</summary>
    public IReadOnlyList<LessonPeriod> Columns { get; }

    /// <summary>Все уроки по звонкам — для переключателя «Все уроки».</summary>
    public IReadOnlyList<LessonPeriod> AllPeriods { get; }

    public IReadOnlyList<WeekLesson> Lessons(DateOnly date, int period) =>
        _cells.TryGetValue((date, period), out var list) ? list : [];

    /// <summary>Все занятия дня по времени — для вида на телефоне (список по дням).</summary>
    public IEnumerable<WeekLesson> Lessons(DateOnly date) =>
        _cells.Where(c => c.Key.Date == date).SelectMany(c => c.Value).OrderBy(l => l.Start);

    /// <param name="monday">Понедельник недели.</param>
    /// <param name="periods">Расписание звонков.</param>
    /// <param name="lessons">Занятия учителя за эту неделю.</param>
    /// <param name="calendar">Производственный календарь.</param>
    /// <param name="vacations">Каникулы, пересекающиеся с неделей.</param>
    /// <param name="today">Сегодня по школьным часам.</param>
    public static TeacherWeek Build(DateOnly monday, IEnumerable<LessonPeriod> periods, IEnumerable<WeekLesson> lessons,
        WorkingDayCalendar calendar, IEnumerable<Vacation> vacations, DateOnly today)
    {
        if (monday.DayOfWeek != DayOfWeek.Monday)
        {
            throw new ArgumentException($"{monday} — не понедельник", nameof(monday));
        }

        var sortedPeriods = periods.OrderBy(p => p.Number).ToList();
        var lessonList = lessons.ToList();
        var vacationList = vacations.ToList();

        var cells = new Dictionary<(DateOnly, int), List<WeekLesson>>();
        foreach (var lesson in lessonList.OrderBy(l => l.Start))
        {
            var period = PeriodFor(sortedPeriods, lesson.StartTime, lesson.DurationMinutes, lesson.LessonNumber);
            if (period is null)
            {
                continue; // звонки не заполнены — сетку всё равно не построить
            }
            var key = (lesson.Date, period.Value);
            if (!cells.TryGetValue(key, out var list))
            {
                cells[key] = list = [];
            }
            list.Add(lesson);
        }

        var used = cells.Keys.Select(k => k.Item2).ToHashSet();
        var columns = sortedPeriods
            .Where(p => p.Number is >= UsualFirst and <= UsualLast || used.Contains(p.Number))
            .ToList();

        // Пн–Пт всегда; суббота — если рабочая по календарю; суббота и воскресенье — если на них стоят занятия.
        var lessonDates = lessonList.Select(l => l.Date).ToHashSet();
        var days = Enumerable.Range(0, 7)
            .Select(monday.AddDays)
            .Where(d => d.DayOfWeek is not (DayOfWeek.Saturday or DayOfWeek.Sunday)
                        || calendar.IsWorkingDay(d) || lessonDates.Contains(d))
            .Select(d => Day(d, calendar, vacationList, today))
            .ToList();

        return new TeacherWeek(monday, days, columns, sortedPeriods, cells);
    }

    /// <summary>
    /// В какой столбец (номер урока) поставить занятие:
    /// указан номер урока — в него; время задано вручную — в урок, с которым больше всего пересекается;
    /// не пересекается ни с одним — в ближайший по времени. При равенстве — в более ранний.
    /// </summary>
    public static int? PeriodFor(IReadOnlyList<LessonPeriod> periods, TimeOnly start, int durationMinutes, int? lessonNumber)
    {
        if (periods.Count == 0)
        {
            return null;
        }
        if (lessonNumber is { } n && periods.Any(p => p.Number == n))
        {
            return n;
        }

        var end = start.AddMinutes(durationMinutes);
        static double Overlap(TimeOnly s1, TimeOnly e1, TimeOnly s2, TimeOnly e2)
        {
            var from = s1 > s2 ? s1 : s2;
            var to = e1 < e2 ? e1 : e2;
            return to > from ? (to - from).TotalMinutes : 0;
        }
        static double Gap(TimeOnly s1, TimeOnly e1, TimeOnly s2, TimeOnly e2) =>
            e1 <= s2 ? (s2 - e1).TotalMinutes : (s1 - e2).TotalMinutes;

        return periods
            .OrderBy(p => p.Number)
            .Select(p => (p.Number, Overlap: Overlap(start, end, p.Start, p.End), Gap: Gap(start, end, p.Start, p.End)))
            .OrderByDescending(x => x.Overlap)
            .ThenBy(x => x.Overlap > 0 ? 0 : x.Gap)
            .ThenBy(x => x.Number)
            .First().Number;
    }

    private static WeekDay Day(DateOnly date, WorkingDayCalendar calendar, List<Vacation> vacations, DateOnly today)
    {
        var active = vacations.Where(v => date >= v.StartDate && date <= v.EndDate).ToList();
        var all = active.FirstOrDefault(v => v.GradeFrom is null && v.GradeTo is null);
        var part = active.Where(v => v.GradeFrom is not null || v.GradeTo is not null)
            .Select(v => $"{v.Name} ({Grades(v)})")
            .ToList();
        return new WeekDay(date, calendar.IsWorkingDay(date), date == today,
            all?.Name, part.Count > 0 ? string.Join(", ", part) : null);
    }

    /// <summary>«1 кл.», «1–4 кл.», «с 5 кл.», «по 4 кл.».</summary>
    private static string Grades(Vacation v) => (v.GradeFrom, v.GradeTo) switch
    {
        ({ } f, { } t) when f == t => $"{f} кл.",
        ({ } f, { } t) => $"{f}–{t} кл.",
        ({ } f, null) => $"с {f} кл.",
        (null, { } t) => $"по {t} кл.",
        _ => "",
    };
}
