using Erudit.Domain.Calendar;
using Erudit.Domain.Lessons;
using Erudit.Domain.School;

namespace Erudit.Tests.Lessons;

public class TeacherWeekTests
{
    // Звонки как в школе (8-й урок — предположение, как в тестовых данных).
    private static readonly LessonPeriod[] Periods =
    [
        P(0, 7, 30, 20), P(1, 8, 0), P(2, 8, 55), P(3, 9, 50), P(4, 10, 40), P(5, 11, 30),
        P(6, 12, 20), P(7, 13, 25), P(8, 14, 15), P(9, 15, 5),
    ];

    private static readonly DateOnly Monday = new(2026, 10, 26);

    private static LessonPeriod P(int number, int h, int m, int minutes = 40) =>
        new() { Number = number, Start = new TimeOnly(h, m), End = new TimeOnly(h, m).AddMinutes(minutes) };

    private static WeekLesson L(DateOnly date, int h, int m, int minutes = 40, int? number = null, int id = 1) =>
        new(id, date.ToDateTime(new TimeOnly(h, m)), minutes, number, "Математика", "Матем", "Каб.31", 2, 5, LessonStatus.Planned, false, null);

    private static TeacherWeek Build(IEnumerable<WeekLesson> lessons, IEnumerable<CalendarDay>? days = null,
        IEnumerable<Vacation>? vacations = null) =>
        TeacherWeek.Build(Monday, Periods, lessons, new WorkingDayCalendar(days ?? []), vacations ?? [], Monday.AddDays(1));

    [Fact]
    public void Номер_урока__занятие_в_своём_столбце()
    {
        // Время даже не совпадает со звонком — главное номер урока.
        Assert.Equal(8, TeacherWeek.PeriodFor(Periods, new TimeOnly(14, 20), 40, 8));
    }

    [Fact]
    public void Время_вручную__в_урок_с_наибольшим_пересечением()
    {
        // 14:30–15:10 пересекается с 8-м уроком (14:15–14:55) на 25 мин, с 9-м (15:05–15:45) на 5 мин.
        Assert.Equal(8, TeacherWeek.PeriodFor(Periods, new TimeOnly(14, 30), 40, null));
        // 15:00–16:00: с 9-м — 40 мин.
        Assert.Equal(9, TeacherWeek.PeriodFor(Periods, new TimeOnly(15, 0), 60, null));
    }

    [Fact]
    public void Время_вручную_вне_уроков__в_ближайший()
    {
        // 16:30 — после всех уроков: ближайший 9-й.
        Assert.Equal(9, TeacherWeek.PeriodFor(Periods, new TimeOnly(16, 30), 40, null));
        // 7:00–7:20 — до 0-го урока (7:30).
        Assert.Equal(0, TeacherWeek.PeriodFor(Periods, new TimeOnly(7, 0), 20, null));
        // 13:05–13:20 — на перемене между 6-м (до 13:00) и 7-м (с 13:25): до 6-го 5 мин, до 7-го 5 мин → ранний.
        Assert.Equal(6, TeacherWeek.PeriodFor(Periods, new TimeOnly(13, 5), 15, null));
    }

    [Fact]
    public void Номер_урока_которого_нет_в_звонках__по_времени()
    {
        Assert.Equal(1, TeacherWeek.PeriodFor(Periods, new TimeOnly(8, 0), 40, 12));
    }

    [Fact]
    public void Без_звонков__столбца_нет()
    {
        Assert.Null(TeacherWeek.PeriodFor([], new TimeOnly(8, 0), 40, 1));
    }

    [Fact]
    public void Столбцы__1_7_всегда__редкие_только_с_занятиями()
    {
        var empty = Build([]);
        Assert.Equal([1, 2, 3, 4, 5, 6, 7], empty.Columns.Select(c => c.Number));
        Assert.Equal(10, empty.AllPeriods.Count);

        var week = Build([L(Monday, 14, 15, number: 8)]);
        Assert.Equal([1, 2, 3, 4, 5, 6, 7, 8], week.Columns.Select(c => c.Number));
    }

    [Fact]
    public void Занятия_раскладываются_по_клеткам()
    {
        var tuesday = Monday.AddDays(1);
        var week = Build([L(tuesday, 13, 25, number: 7, id: 1), L(tuesday, 7, 30, 20, number: 0, id: 2)]);

        Assert.Equal(1, Assert.Single(week.Lessons(tuesday, 7)).Id);
        Assert.Equal(2, Assert.Single(week.Lessons(tuesday, 0)).Id);
        Assert.Empty(week.Lessons(Monday, 7));
        // Список дня для телефона — по времени.
        Assert.Equal([2, 1], week.Lessons(tuesday).Select(l => l.Id));
    }

    [Fact]
    public void Дни__пн_пт__суббота_только_рабочая_или_с_занятиями()
    {
        var saturday = Monday.AddDays(5);
        Assert.Equal(5, Build([]).Days.Count);

        var workingSaturday = Build([], [new CalendarDay { Date = saturday, IsWorkingDay = true }]);
        Assert.Equal(saturday, workingSaturday.Days[^1].Date);

        var lessonOnSaturday = Build([L(saturday, 10, 0)]);
        Assert.Equal(6, lessonOnSaturday.Days.Count);
        Assert.False(lessonOnSaturday.Days[^1].IsWorking);
    }

    [Fact]
    public void Дни__нерабочий_сегодня_каникулы()
    {
        var wednesday = Monday.AddDays(2);
        var week = Build([],
            [new CalendarDay { Date = wednesday, IsWorkingDay = false }],
            [
                new Vacation { Name = "Осенние", StartDate = Monday.AddDays(3), EndDate = Monday.AddDays(10) },
                new Vacation { Name = "Доп. каникулы", StartDate = Monday, EndDate = Monday, GradeFrom = 1, GradeTo = 1 },
            ]);

        Assert.False(week.Days[2].IsWorking);
        Assert.True(week.Days[1].IsToday);
        Assert.Equal("Доп. каникулы (1 кл.)", week.Days[0].VacationPart);
        Assert.Null(week.Days[0].VacationAll);
        Assert.Equal("Осенние", week.Days[3].VacationAll);
    }

    [Fact]
    public void Не_понедельник__ошибка()
    {
        Assert.Throws<ArgumentException>(() =>
            TeacherWeek.Build(Monday.AddDays(1), Periods, [], WorkingDayCalendar.Empty, [], Monday));
    }
}
