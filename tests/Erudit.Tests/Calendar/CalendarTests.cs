using Erudit.Domain.Calendar;

namespace Erudit.Tests.Calendar;

public class WorkingDayCalendarTests
{
    [Theory]
    [InlineData("2026-10-19", true)]  // понедельник
    [InlineData("2026-10-23", true)]  // пятница
    [InlineData("2026-10-24", false)] // суббота
    [InlineData("2026-10-25", false)] // воскресенье
    public void Без_исключений_рабочие_только_будни(string date, bool expected)
    {
        Assert.Equal(expected, WorkingDayCalendar.Empty.IsWorkingDay(DateOnly.Parse(date)));
    }

    [Fact]
    public void Исключение_из_календаря_важнее_дня_недели()
    {
        var calendar = new WorkingDayCalendar(
        [
            new CalendarDay { Date = new DateOnly(2026, 11, 4), IsWorkingDay = false }, // праздник в среду
            new CalendarDay { Date = new DateOnly(2026, 11, 7), IsWorkingDay = true },  // рабочая суббота
        ]);

        Assert.False(calendar.IsWorkingDay(new DateOnly(2026, 11, 4)));
        Assert.True(calendar.IsWorkingDay(new DateOnly(2026, 11, 7)));
    }
}

public class VacationTests
{
    private static readonly Vacation FirstGradeFebruary = new()
    {
        Name = "Дополнительные каникулы для 1-х классов",
        StartDate = new DateOnly(2027, 2, 15),
        EndDate = new DateOnly(2027, 2, 21),
        GradeFrom = 1,
        GradeTo = 1,
    };

    [Fact]
    public void Каникулы_первоклассников_не_касаются_второго_класса()
    {
        var date = new DateOnly(2027, 2, 17);

        Assert.True(FirstGradeFebruary.AppliesTo(date, grade: 1));
        Assert.False(FirstGradeFebruary.AppliesTo(date, grade: 2));
    }

    [Fact]
    public void Границы_каникул_включаются()
    {
        Assert.True(FirstGradeFebruary.AppliesTo(new DateOnly(2027, 2, 15), 1));
        Assert.True(FirstGradeFebruary.AppliesTo(new DateOnly(2027, 2, 21), 1));
        Assert.False(FirstGradeFebruary.AppliesTo(new DateOnly(2027, 2, 22), 1));
    }

    [Fact]
    public void Без_параллелей_каникулы_для_всех()
    {
        var autumn = new Vacation { StartDate = new DateOnly(2026, 10, 26), EndDate = new DateOnly(2026, 11, 3) };

        Assert.True(autumn.AppliesTo(new DateOnly(2026, 10, 30), 1));
        Assert.True(autumn.AppliesTo(new DateOnly(2026, 10, 30), 11));
    }
}
