using Erudit.Domain.Calendar;
using Erudit.Domain.Lessons;

namespace Erudit.Tests.Lessons;

public class SeriesPlannerTests
{
    // 4 ноября 2026 — среда, праздник (День народного единства).
    private static readonly WorkingDayCalendar Calendar2026 = new(
    [
        new CalendarDay { Date = new DateOnly(2026, 11, 4), IsWorkingDay = false },
    ]);

    private static Series WednesdaySeries(DateOnly from, DateOnly to) => new()
    {
        DayOfWeek = DayOfWeek.Wednesday,
        LessonNumber = 7,
        StartTime = new TimeOnly(13, 25),
        DurationMinutes = 40,
        FromDate = from,
        ToDate = to,
    };

    [Fact]
    public void Праздник_пропускается()
    {
        var series = WednesdaySeries(new DateOnly(2026, 10, 28), new DateOnly(2026, 11, 25));

        var dates = SeriesPlanner.GetLessonDates(series, Calendar2026);

        Assert.Equal(
        [
            new DateOnly(2026, 10, 28),
            new DateOnly(2026, 11, 11),
            new DateOnly(2026, 11, 18),
            new DateOnly(2026, 11, 25),
        ], dates);
    }

    [Fact]
    public void Начало_серии_не_в_нужный_день_недели__первое_занятие_в_ближайшую_среду()
    {
        // 26.10.2026 — понедельник
        var series = WednesdaySeries(new DateOnly(2026, 10, 26), new DateOnly(2026, 10, 31));

        var dates = SeriesPlanner.GetLessonDates(series, Calendar2026);

        Assert.Equal([new DateOnly(2026, 10, 28)], dates);
    }

    [Fact]
    public void Дата_окончания_включается()
    {
        var series = WednesdaySeries(new DateOnly(2026, 11, 18), new DateOnly(2026, 11, 25));

        var dates = SeriesPlanner.GetLessonDates(series, Calendar2026);

        Assert.Contains(new DateOnly(2026, 11, 25), dates);
    }

    [Fact]
    public void Серия_на_субботу_при_пятидневке_пустая()
    {
        var series = WednesdaySeries(new DateOnly(2026, 10, 1), new DateOnly(2026, 10, 31));
        series.DayOfWeek = DayOfWeek.Saturday;

        var dates = SeriesPlanner.GetLessonDates(series, WorkingDayCalendar.Empty);

        Assert.Empty(dates);
    }

    [Fact]
    public void Окончание_раньше_начала__ошибка()
    {
        var series = WednesdaySeries(new DateOnly(2026, 11, 25), new DateOnly(2026, 11, 1));

        Assert.Throws<ArgumentException>(() => SeriesPlanner.GetLessonDates(series, Calendar2026));
    }

    [Fact]
    public void Занятия_получают_время_в_UTC_и_поля_серии()
    {
        var series = WednesdaySeries(new DateOnly(2026, 11, 11), new DateOnly(2026, 11, 11));
        series.TeacherId = 5;
        series.SubjectId = 6;
        series.RoomId = 7;
        series.Capacity = 3;

        var lesson = Assert.Single(SeriesPlanner.CreateLessons(series, Calendar2026));

        // 13:25 по Москве (UTC+3) = 10:25 UTC
        Assert.Equal(new DateTime(2026, 11, 11, 10, 25, 0, DateTimeKind.Utc), lesson.StartsAt);
        Assert.Equal(DateTimeKind.Utc, lesson.StartsAt.Kind);
        Assert.Equal((5, 6, 7, 3, 40, 7), (lesson.TeacherId, lesson.SubjectId, lesson.RoomId,
            lesson.Capacity, lesson.DurationMinutes, lesson.LessonNumber));
        Assert.Equal(LessonStatus.Planned, lesson.Status);
    }
}
