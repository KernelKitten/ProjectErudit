using ClosedXML.Excel;
using Erudit.Domain.People;
using Erudit.Infrastructure.Import;
using Erudit.Infrastructure.People;
using Erudit.Tests.Database;
using Microsoft.EntityFrameworkCore;

namespace Erudit.Tests.Import;

/// <summary>Excel-файлы для тестов создаём прямо в коде — так видно, что именно лежит в файле.</summary>
public static class TestExcel
{
    /// <summary>Лист с таблицей: rows[0] — заголовки. topRows — пустые/заголовочные строки над таблицей.</summary>
    public static MemoryStream Create(string[][] rows, int topRows = 0, string sheetName = "Лист1", bool extraFirstSheet = false)
    {
        using var workbook = new XLWorkbook();
        if (extraFirstSheet)
        {
            workbook.AddWorksheet("Инструкция").Cell(1, 1).Value = "Здесь просто текст";
        }
        var sheet = workbook.AddWorksheet(sheetName);
        if (topRows > 0)
        {
            sheet.Cell(1, 1).Value = "Список учителей гимназии";
        }
        for (var r = 0; r < rows.Length; r++)
        {
            for (var c = 0; c < rows[r].Length; c++)
            {
                // Число в ячейке — как бывает, когда телефон вбили без «+»: Excel хранит его числом.
                if (long.TryParse(rows[r][c], out var number))
                {
                    sheet.Cell(topRows + r + 1, c + 1).Value = number;
                }
                else
                {
                    sheet.Cell(topRows + r + 1, c + 1).Value = rows[r][c];
                }
            }
        }
        var stream = new MemoryStream();
        workbook.SaveAs(stream);
        stream.Position = 0;
        return stream;
    }
}

public class ExcelTableReaderTests
{
    [Fact]
    public void Таблица_не_с_первой_строки_на_втором_листе_с_другими_названиями_колонок()
    {
        using var file = TestExcel.Create(
        [
            ["№", "Ф.И.О. учителя", "Предмет", "Тел."],
            ["1", "Иванова Мария Петровна", "Математика", "79991234567"],
            ["", "", "", ""], // пустая строка в середине
            ["2", "Петров Илья", "Физика", ""],
        ], topRows: 2, extraFirstSheet: true);

        var table = ExcelTableReader.Read(file, TeacherService.Columns);

        Assert.Equal("Лист1", table.SheetName);
        Assert.Equal(3, table.HeaderRow);
        Assert.Equal(["Иванова Мария Петровна", "Петров Илья"], table.Rows.Select(r => r.Get("name")));
        Assert.Equal("79991234567", table.Rows[0].Get("phone")); // число из ячейки — без «7,99E+10»
        Assert.Equal(6, table.Rows[1].RowNumber); // номер строки как в Excel — чтобы показать человеку
    }

    [Fact]
    public void Нет_обязательной_колонки__понятная_ошибка()
    {
        using var file = TestExcel.Create([["Фамилия", "Телефон"], ["Иванова", "79991234567"]]);

        var error = Assert.Throws<FormatException>(() => ExcelTableReader.Read(file, TeacherService.Columns));

        Assert.Contains("ФИО", error.Message);
    }

    [Fact]
    public void Не_xlsx__понятная_ошибка()
    {
        using var notExcel = new MemoryStream("ФИО;Телефон\nИванова Мария;79991234567"u8.ToArray()); // это CSV

        var error = Assert.Throws<FormatException>(() => ExcelTableReader.Read(notExcel, TeacherService.Columns));

        Assert.Contains(".xlsx", error.Message);
    }
}

[Collection(DatabaseCollection.Name)]
public class TeacherImportTests(TestDatabase database)
{
    [DatabaseFact]
    public async Task Предпросмотр__ошибки_предупреждения_и_существующий_родитель()
    {
        await using var db = database.CreateContext();
        var parent = await db.People.FirstAsync(p => p.Phone != null && p.Roles.Any(r => r.Role == Role.Parent)
                                                     && !p.Roles.Any(r => r.Role != Role.Parent));
        var student = await db.People.FirstAsync(p => p.Phone != null && p.Roles.Any(r => r.Role == Role.Student));

        using var file = TestExcel.Create(
        [
            ["ФИО", "Телефон"],
            ["Новикова Ольга Игоревна", "8 (900) 555-01-01"],          // 2: новый
            ["Смирнов Павел", ""],                                     // 3: без телефона — предупреждение
            ["Кузнецов", "79005550102"],                               // 4: нет имени — ошибка
            ["Попова Анна", "12345"],                                  // 5: плохой телефон — ошибка
            [parent.FullName, parent.Phone!],                          // 6: родитель → станет учителем
            ["Совсем Другой Человек", student.Phone!],                 // 7: номер ученика — ошибка
            ["Дубль Один", "79005550199"],                             // 8 и 9: телефон повторяется
            ["Дубль Два", "+7 900 555-01-99"],
        ]);

        var preview = await new TeacherService(db).PreviewAsync(file);
        TeacherImportRow Row(int n) => preview.Rows.Single(r => r.RowNumber == n);

        Assert.Empty(Row(2).Errors);
        Assert.Equal("+79005550101", Row(2).Phone);
        Assert.Empty(Row(3).Errors);
        Assert.Contains(Row(3).Warnings, w => w.Contains("Нет телефона"));
        Assert.NotEmpty(Row(4).Errors);
        Assert.NotEmpty(Row(5).Errors);
        Assert.Equal(parent.Id, Row(6).ExistingPersonId);
        Assert.Contains(Row(6).Warnings, w => w.Contains("станет и учителем"));
        Assert.Contains(Row(7).Errors, e => e.Contains("ученика"));
        Assert.Contains(Row(8).Errors, e => e.Contains("повторяется"));
        Assert.True(preview.HasErrors);
        await Assert.ThrowsAsync<InvalidOperationException>(() => new TeacherService(db).ApplyAsync(preview));
    }

    [DatabaseFact]
    public async Task Загрузка__новые_учителя_и_роль_для_родителя()
    {
        Person parent;
        await using (var db = database.CreateContext())
        {
            parent = await db.People.OrderByDescending(p => p.Id)
                .FirstAsync(p => p.Phone != null && p.Roles.Count == 1 && p.Roles.Any(r => r.Role == Role.Parent));
        }

        using var file = TestExcel.Create(
        [
            ["ФИО", "Телефон"],
            ["Федорова Ксения Андреевна", "79005550201"],
            [parent.FullName.ToUpperInvariant(), parent.Phone!], // регистр не важен
        ]);

        await using (var db = database.CreateContext())
        {
            var service = new TeacherService(db);
            var preview = await service.PreviewAsync(file);
            Assert.False(preview.HasErrors);
            Assert.Equal(2, await service.ApplyAsync(preview));
        }

        await using var check = database.CreateContext();
        var created = await check.People.Include(p => p.Roles).SingleAsync(p => p.Phone == "+79005550201");
        Assert.Equal(("Федорова", "Ксения", "Андреевна"), (created.LastName, created.FirstName, created.MiddleName));
        Assert.True(created.HasRole(Role.Teacher));
        var updatedParent = await check.People.Include(p => p.Roles).SingleAsync(p => p.Id == parent.Id);
        Assert.True(updatedParent.HasRole(Role.Teacher) && updatedParent.HasRole(Role.Parent));
    }
}
