using Erudit.Domain.Lessons;
using Erudit.Infrastructure.Lessons;
using Erudit.Tests.Database;
using Microsoft.EntityFrameworkCore;

namespace Erudit.Tests.Lessons;

/// <summary>Неделя учителя по тестовым данным (DevDataSeeder): только читаем, ничего не меняем.</summary>
[Collection(DatabaseCollection.Name)]
public class TeacherWeekServiceTests(TestDatabase database)
{
    private sealed class FixedTime(DateTime utc) : TimeProvider
    {
        public override DateTimeOffset GetUtcNow() => utc;
    }

    private TeacherWeekService Service() => new(database.CreateFactory(), new FixedTime(TestDatabase.Now));

    private async Task<int> TeacherIdAsync(string lastName)
    {
        await using var db = database.CreateContext();
        return await db.People.Where(p => p.LastName == lastName).Select(p => p.Id).SingleAsync();
    }

    [Fact]
    public void Понедельник_недели()
    {
        Assert.Equal(new DateOnly(2026, 10, 26), TeacherWeekService.MondayOf(new DateOnly(2026, 10, 26)));
        Assert.Equal(new DateOnly(2026, 10, 26), TeacherWeekService.MondayOf(new DateOnly(2026, 11, 1)));
    }

    [DatabaseFact]
    public async Task Неделя_на_каникулах__серия_и_разовое_со_временем_вручную()
    {
        var monday = new DateOnly(2026, 10, 26);
        var week = await Service().LoadAsync(await TeacherIdAsync("Соколова"), monday);

        // Серия по средам, 8-й урок: 7 учеников из 8 мест.
        var series = Assert.Single(week.Lessons(monday.AddDays(2), 8));
        Assert.True(series.IsSeries);
        Assert.Equal((7, 8), (series.Enrolled, series.Capacity));
        Assert.Contains(week.Columns, c => c.Number == 8);

        // Разовое в пятницу 10:00–11:00, время вручную: больше всего пересекается с 3-м уроком (9:50–10:30).
        var single = Assert.Single(week.Lessons(monday.AddDays(4), 3));
        Assert.False(single.IsSeries);
        Assert.Equal(new TimeOnly(10, 0), single.StartTime);

        // Осенние каникулы — всю неделю.
        Assert.All(week.Days, d => Assert.Equal("Осенние каникулы", d.VacationAll));
    }

    [DatabaseFact]
    public async Task Неделя__сегодня_и_отменённое_занятие()
    {
        // «Сейчас» в тестах — суббота 17.10, она в предыдущей неделе: в этой «сегодня» нет.
        var week = await Service().LoadAsync(await TeacherIdAsync("Орлов"), new DateOnly(2026, 10, 19));

        Assert.DoesNotContain(week.Days, d => d.IsToday);
        var cancelled = Assert.Single(week.Lessons(new DateOnly(2026, 10, 22), 7));
        Assert.Equal(LessonStatus.Cancelled, cancelled.Status);
    }
}
