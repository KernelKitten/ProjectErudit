using ClosedXML.Excel;
using Erudit.Domain.Lessons;
using Erudit.Infrastructure.People;
using Erudit.Infrastructure.Reports;

namespace Erudit.Tests.Reports;

/// <summary>Выгрузка в Excel: файл открывается, листы как вкладки на экране, цифры — те же, что в отчёте. Без базы.</summary>
public class ExcelReportsTests
{
    private static readonly DateTime Generated = new(2026, 11, 14, 18, 30, 0);

    private static XLWorkbook Open(byte[] bytes) => new(new MemoryStream(bytes));

    /// <summary>Строка листа, в первой ячейке которой стоит текст.</summary>
    private static IXLRow RowStarting(IXLWorksheet sheet, string text) =>
        sheet.RowsUsed().First(r => r.Cell(1).GetString() == text);

    [Theory]
    [InlineData("2026-10-01", "2026-10-31", "Октябрь 2026")]
    [InlineData("2026-10-01", "2026-10-15", "01.10.2026–15.10.2026")]
    [InlineData("2026-11-01", "2027-03-31", "01.11.2026–31.03.2027")]
    public void Период_в_шапке(string from, string to, string expected) =>
        Assert.Equal(expected, ExcelReports.PeriodLabel(DateOnly.Parse(from), DateOnly.Parse(to)));

    [Fact]
    public void Имя_файла_без_запрещённых_символов() =>
        Assert.Equal("Отчёт_ 7а _ Октябрь 2026.xlsx", ExcelReports.FileName("Отчёт: 7а / Октябрь 2026"));

    [Fact]
    public void Отчёт_учителя__четыре_листа__часы_числом()
    {
        var day = new DateOnly(2026, 10, 5);
        ReportStudent Kid(int id, Attendance att) => new(id, $"Ученик {id}", "7а", att);
        var report = TeacherReport.Build(
        [
            new ReportLesson(1, day, new TimeOnly(14, 15), 8, "Математика", "Каб.1", LessonStatus.Held, true, 60, [Kid(1, Attendance.Present)]),
            new ReportLesson(2, day.AddDays(1), new TimeOnly(14, 15), 8, "Математика", "Каб.1", LessonStatus.Held, true, 40,
                [Kid(1, Attendance.Present), Kid(2, Attendance.Absent)]),
            new ReportLesson(3, day.AddDays(2), new TimeOnly(14, 15), null, "Математика", "Каб.1", LessonStatus.Planned, true, 40, []),
        ]);

        using var book = Open(ExcelReports.Teacher(report, "Иванова М.П.", "Октябрь 2026", Generated));

        Assert.Equal(["Итоги", "Занятия", "Ученики", "Классы"], book.Worksheets.Select(s => s.Name));
        var totals = book.Worksheet("Итоги");
        Assert.Equal("Отчёт учителя: Иванова М.П.", totals.Cell(1, 1).GetString());
        Assert.Equal("Октябрь 2026", totals.Cell(2, 1).GetString());
        var total = RowStarting(totals, "Итого, ак. ч.").Cell(2);
        Assert.Equal(2.5, total.GetDouble()); // (60 + 40) / 40 — числом, не текстом
        Assert.Equal("0.00", total.Style.NumberFormat.Format);

        var lessons = book.Worksheet("Занятия");
        Assert.Equal(["Дата", "Время", "Предмет", "Кабинет", "Вид", "Записано", "Было", "Минут", "Ак. ч.", "Отметка"],
            lessons.Row(5).CellsUsed().Select(c => c.GetString()));
        Assert.Equal(new DateTime(2026, 10, 5), lessons.Cell(6, 1).GetDateTime());
        Assert.Equal("Не отмечено", lessons.Cell(8, 10).GetString());
        Assert.Equal("—", lessons.Cell(8, 9).GetString());

        var students = book.Worksheet("Ученики");
        Assert.Equal(2.5, RowStarting(students, "Ученик 1").Cell(5).GetDouble());
    }

    [Fact]
    public void Сводка__итоговая_строка__уволенный_помечен()
    {
        TeacherSummaryRow[] rows =
        [
            new(1, "Иванова М.П.", true, 3, 1, 0, 1, 40, 80, 120),
            new(2, "Петров П.П.", false, 1, 0, 2, 0, 0, 40, 40),
        ];
        using var book = Open(ExcelReports.TeacherSummary(rows, "Октябрь 2026", "Математика", Generated));
        var sheet = book.Worksheet("Сводка");

        Assert.Equal("Октябрь 2026 · Математика", sheet.Cell(2, 1).GetString());
        Assert.Contains(sheet.RowsUsed(), r => r.Cell(1).GetString() == "Петров П.П. (уволен(а))");
        var total = RowStarting(sheet, "Итого");
        Assert.Equal((4, 4.0), (total.Cell(2).GetValue<int>(), total.Cell(8).GetDouble()));
        Assert.True(total.Cell(1).Style.Font.Bold);
    }

    [Fact]
    public void Отчёт_по_классу__ученики_и_разбивка()
    {
        var report = ClassReport.Build(
            [new ClassReportStudent(1, "Аистов Пётр", null), new ClassReportStudent(2, "Бобров Иван", "до 30.09")],
            [
                new ClassReportVisit(1, "Математика", "Иванова М.П.", LessonStatus.Held, true, Attendance.Present, 40),
                new ClassReportVisit(1, "Физика", "Петров П.П.", LessonStatus.Held, true, Attendance.Absent, 40),
            ]);
        using var book = Open(ExcelReports.Class(report, "7а", "Октябрь 2026", Generated));

        Assert.Equal(["Ученики", "По предметам"], book.Worksheets.Select(s => s.Name));
        Assert.Equal("Отчёт по классу 7а", book.Worksheet("Ученики").Cell(1, 1).GetString());
        Assert.Equal("до 30.09", RowStarting(book.Worksheet("Ученики"), "Бобров Иван").Cell(2).GetString());
        Assert.Equal(1.0, RowStarting(book.Worksheet("Ученики"), "Итого по классу").Cell(6).GetDouble());
        Assert.Equal(2, book.Worksheet("По предметам").RowsUsed().Count(r => r.Cell(1).GetString() == "Аистов Пётр"));
    }

    [Fact]
    public void Проверка_данных__что_поправить_и_сводка()
    {
        var report = new DataCheckReport(
            [new DataCheckClassRow(1, "7а", null, 10, 8, 5, 12, 6)],
            ["7а"],
            [new DataCheckStudent(5, "Волков Егор", 1, "7а")],
            ["Петров П.П."],
            [new UnknownPhoneAttempt("+79991234567", new DateTime(2026, 11, 1, 9, 5, 0), 2, NowKnown: true)]);
        using var book = Open(ExcelReports.DataCheck(report, "2026/27", Generated));

        var problems = book.Worksheet("Что поправить");
        Assert.Equal(4, problems.RowsUsed().Count(r => r.RowNumber() > 5));
        Assert.Contains("номер уже добавлен", RowStarting(problems, "Вход с неизвестного номера").Cell(4).GetString());
        var classes = book.Worksheet("Сводка по классам");
        Assert.Equal("не назначен", classes.Cell(6, 2).GetString());
        Assert.Equal(10, RowStarting(classes, "Всего").Cell(3).GetValue<int>());
    }
}
