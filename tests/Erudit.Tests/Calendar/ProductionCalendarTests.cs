using System.Text;
using Erudit.Domain.Calendar;
using Erudit.Infrastructure.Calendar;
using Erudit.Tests.Database;
using Microsoft.EntityFrameworkCore;

namespace Erudit.Tests.Calendar;

public class ProductionCalendarTests
{
    private static DateOnly D(string s) => DateOnly.Parse(s);

    [Fact]
    public void Праздник_в_будни_и_рабочая_суббота__исключения()
    {
        // 2026: 04.11 — среда (праздник); 31.10 — суббота, представим, что она рабочая (её нет в holidays).
        var holidays = new[] { D("2026-11-04"), D("2026-11-01") /* обычное воскресенье */ }
            .Concat(AllWeekends(2026).Where(d => d != D("2026-10-31")));

        var result = ProductionCalendar.ToExceptions(holidays, [], fromYear: 2026);

        Assert.Equal([2026], result.Years);
        Assert.Contains(result.Exceptions, d => d.Date == D("2026-11-04") && !d.IsWorkingDay);
        Assert.Contains(result.Exceptions, d => d.Date == D("2026-10-31") && d.IsWorkingDay);
        Assert.Equal(2, result.Exceptions.Count); // обычные выходные — не исключения
    }

    [Fact]
    public void Нерабочие_дни_по_указу_тоже_учитываются()
    {
        var result = ProductionCalendar.ToExceptions(AllWeekends(2026), [D("2026-05-04")], fromYear: 2026);

        Assert.Equal([D("2026-05-04")], result.Exceptions.Select(d => d.Date));
    }

    [Fact]
    public void Старые_годы_пропускаются()
    {
        var result = ProductionCalendar.ToExceptions([D("2025-01-01"), D("2026-01-01")], [], fromYear: 2026);

        Assert.Equal([2026], result.Years);
        Assert.DoesNotContain(result.Exceptions, d => d.Date.Year == 2025);
    }

    public static IEnumerable<DateOnly> AllWeekends(int year)
    {
        for (var d = new DateOnly(year, 1, 1); d.Year == year; d = d.AddDays(1))
        {
            if (d.DayOfWeek is DayOfWeek.Saturday or DayOfWeek.Sunday)
            {
                yield return d;
            }
        }
    }
}

[Collection(DatabaseCollection.Name)]
public class ProductionCalendarImporterTests(TestDatabase database)
{
    private static Stream Json(IEnumerable<DateOnly> holidays) => new MemoryStream(Encoding.UTF8.GetBytes(
        $$"""{"holidays": [{{string.Join(",", holidays.Select(d => $"\"{d:yyyy-MM-dd}\""))}}], "preholidays": [], "nowork": []}"""));

    [DatabaseFact]
    public async Task Загрузка_не_трогает_ручные_правки_и_убирает_лишнее()
    {
        var weekends = ProductionCalendarTests.AllWeekends(2027).ToList();
        await using (var db = database.CreateContext())
        {
            // Ручная правка: администратор сделал 07.06.2027 (понедельник) нерабочим.
            db.CalendarDays.Add(new CalendarDay { Date = new DateOnly(2027, 6, 7), IsWorkingDay = false, IsManual = true });
            await db.SaveChangesAsync();
        }

        // Файл: выходные 2027 + праздник 08.03.2027 (понедельник). В тестовых данных были и другие праздники 2027.
        await using (var db = database.CreateContext())
        {
            var result = await new ProductionCalendarImporter(db).ImportAsync(Json(weekends.Append(new DateOnly(2027, 3, 8))));
            Assert.Equal([2027], result.Years);
        }

        await using var check = database.CreateContext();
        var days2027 = await check.CalendarDays.Where(d => d.Date.Year == 2027).OrderBy(d => d.Date).ToListAsync();
        Assert.Equal([new DateOnly(2027, 3, 8), new DateOnly(2027, 6, 7)], days2027.Select(d => d.Date));
        Assert.True(days2027.Single(d => d.Date.Month == 6).IsManual);
        // 2026 год в файле не описан — его исключения остались.
        Assert.True(await check.CalendarDays.AnyAsync(d => d.Date == new DateOnly(2026, 11, 4)));
    }

    [DatabaseFact]
    public async Task Неправильный_файл__понятная_ошибка_и_ничего_не_меняется()
    {
        await using var db = database.CreateContext();
        var before = await db.CalendarDays.CountAsync();

        var error = await Assert.ThrowsAsync<FormatException>(() =>
            new ProductionCalendarImporter(db).ImportAsync(new MemoryStream("{\"users\": []}"u8.ToArray())));

        Assert.Contains("holidays", error.Message);
        Assert.Equal(before, await db.CalendarDays.CountAsync());
    }
}
