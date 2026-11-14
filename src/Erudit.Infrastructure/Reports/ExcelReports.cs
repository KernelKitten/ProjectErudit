using System.Globalization;
using ClosedXML.Excel;
using Erudit.Domain.Lessons;
using Erudit.Infrastructure.People;

namespace Erudit.Infrastructure.Reports;

/// <summary>
/// Выгрузка отчётов в Excel (этап 7, шаг 4, решено 2026-11-06): файл повторяет экран — каждая вкладка отчёта
/// отдельный лист; на каждом листе шапка с названием, периодом и временем выгрузки, жирные закреплённые заголовки,
/// часы в формате «0,00». Цифры берём из тех же объектов, что и экран, — расхождений быть не может.
/// </summary>
public static class ExcelReports
{
    private static readonly CultureInfo Ru = CultureInfo.GetCultureInfo("ru-RU");

    /// <summary>Период для шапки и имени файла: целый месяц — «Октябрь 2026», иначе «01.10.2026–15.10.2026».</summary>
    public static string PeriodLabel(DateOnly from, DateOnly to)
    {
        if (from.Day == 1 && to == from.AddMonths(1).AddDays(-1))
        {
            var month = from.ToString("MMMM yyyy", Ru);
            return char.ToUpper(month[0], Ru) + month[1..];
        }
        return $"{from:dd.MM.yyyy}–{to:dd.MM.yyyy}";
    }

    /// <summary>Имя файла без символов, которые Windows не разрешает: «Отчёт по классу 7а — Октябрь 2026.xlsx».</summary>
    public static string FileName(string name)
    {
        var bad = Path.GetInvalidFileNameChars().Concat(['/', '\\', ':', '*', '?', '"', '<', '>', '|']).ToHashSet();
        return new string(name.Select(c => bad.Contains(c) ? '_' : c).ToArray()).Trim() + ".xlsx";
    }

    // ---------------------------------------------------------------- Отчёт учителя

    public static byte[] Teacher(TeacherReport r, string teacherName, string period, DateTime generatedAt)
    {
        using var book = new XLWorkbook();
        var title = $"Отчёт учителя: {teacherName}";

        var totals = new SheetWriter(book, "Итоги", title, period, generatedAt);
        totals.Header("Показатель", "Значение");
        totals.Row("Итого, ак. ч.", r.TotalHours);
        foreach (var kind in r.Kinds)
        {
            totals.Row($"{KindName(kind.Kind)}: занятий", kind.Lessons);
            totals.Row($"{KindName(kind.Kind)}: ак. ч.", kind.Hours);
        }
        totals.Row("Проведено", r.Held);
        totals.Row("Не состоялось", r.NotHeld);
        totals.Row("Не отмечено", r.Unmarked);
        totals.Row("Отменено", r.Cancelled);
        totals.Row("Впереди", r.Upcoming);
        totals.Note($"В часы входят только проведённые занятия; 1 ак. ч. = {AcademicHours.MinutesPerHour} минут, итог округлён до сотых.");
        totals.Finish();

        var lessons = new SheetWriter(book, "Занятия", title, period, generatedAt);
        lessons.Header("Дата", "Время", "Предмет", "Кабинет", "Вид", "Записано", "Было", "Минут", "Ак. ч.", "Отметка");
        foreach (var l in r.Lessons)
        {
            var held = l.Status == LessonStatus.Held;
            lessons.Row(l.Date, (l.LessonNumber is { } n ? $"{n} урок, " : "") + l.Start.ToString("H:mm"), l.Subject, l.Room,
                KindName(l.Kind), l.Students.Count,
                held ? l.Students.Count(s => s.Attendance == Attendance.Present) : null,
                held ? l.Minutes : null, held ? AcademicHours.FromMinutes(l.Minutes) : null,
                l.IsUnmarked ? "Не отмечено" : StatusName(l.Status));
        }
        lessons.Finish();

        var students = new SheetWriter(book, "Ученики", title, period, generatedAt);
        students.Header("Ученик", "Класс", "Был(а)", "Не был(а)", "Ак. ч.");
        foreach (var s in r.Students)
        {
            students.Row(s.Name, s.ClassName ?? "—", s.Attended, s.Absent, s.Hours);
        }
        students.Finish();

        var classes = new SheetWriter(book, "Классы", title, period, generatedAt);
        classes.Header("Класс", "Учеников", "Посещений", "Пропусков", "Ак. ч. учеников");
        foreach (var c in r.Classes)
        {
            classes.Row(c.ClassName ?? "Без класса", c.Students, c.Attended, c.Absent, c.Hours);
        }
        classes.Finish();

        return Save(book);
    }

    // ---------------------------------------------------------------- Сводка завуча

    public static byte[] TeacherSummary(IReadOnlyCollection<TeacherSummaryRow> rows, string period, string? subject, DateTime generatedAt)
    {
        using var book = new XLWorkbook();
        var sheet = new SheetWriter(book, "Сводка", "Сводка по учителям", subject is null ? period : $"{period} · {subject}", generatedAt);
        sheet.Header("Учитель", "Проведено", "Не состоялось", "Отменено", "Не отмечено", "Инд., ак. ч.", "Груп., ак. ч.", "Всего, ак. ч.");
        foreach (var r in rows)
        {
            sheet.Row(r.IsActive ? r.Name : $"{r.Name} (уволен(а))", r.Held, r.NotHeld, r.Cancelled, r.Unmarked,
                r.IndividualHours, r.GroupHours, r.TotalHours);
        }
        var t = TeacherSummaryRow.Total(rows);
        sheet.Total("Итого", t.Held, t.NotHeld, t.Cancelled, t.Unmarked, t.IndividualHours, t.GroupHours, t.TotalHours);
        sheet.Note("В часы входят только проведённые занятия; «Всего» включает и проведённые без учеников. " +
                   $"1 ак. ч. = {AcademicHours.MinutesPerHour} минут, итоги округлены до сотых.");
        sheet.Finish();
        return Save(book);
    }

    // ---------------------------------------------------------------- Отчёт по классу

    public static byte[] Class(ClassReport r, string className, string period, DateTime generatedAt)
    {
        using var book = new XLWorkbook();
        var title = $"Отчёт по классу {className}";

        var students = new SheetWriter(book, "Ученики", title, period, generatedAt);
        students.Header("Ученик", "В классе", "Проведено", "Был(а)", "Не был(а)", "Ак. ч.", "Впереди");
        foreach (var row in r.Rows)
        {
            var c = row.Counts;
            students.Row(row.Student.Name, row.Student.Note ?? "", c.Held, c.Attended, c.Absent, c.Hours, c.Upcoming);
        }
        students.Total("Итого по классу", "", r.Total.Held, r.Total.Attended, r.Total.Absent, r.Total.Hours, r.Total.Upcoming);
        students.Note("«Проведено» — проведённые занятия, на которые ученик был записан; в часы входят только те, где он был. " +
                      "Перешедшие в другой класс считаются только за время в этом классе.");
        students.Finish();

        var lines = new SheetWriter(book, "По предметам", title, period, generatedAt);
        lines.Header("Ученик", "Предмет", "Учитель", "Проведено", "Был(а)", "Не был(а)", "Ак. ч.", "Впереди");
        foreach (var row in r.Rows)
        {
            foreach (var l in row.Lines)
            {
                var c = l.Counts;
                lines.Row(row.Student.Name, l.Subject, l.Teacher, c.Held, c.Attended, c.Absent, c.Hours, c.Upcoming);
            }
        }
        lines.Finish();
        return Save(book);
    }

    // ---------------------------------------------------------------- Проверка данных

    public static byte[] DataCheck(DataCheckReport r, string yearName, DateTime generatedAt)
    {
        using var book = new XLWorkbook();
        var title = "Проверка данных";
        var subtitle = $"Учебный год {yearName}";

        var problems = new SheetWriter(book, "Что поправить", title, subtitle, generatedAt);
        problems.Header("Что не так", "Класс", "Кто / что", "Подробности");
        foreach (var name in r.ClassesWithoutTeacher)
        {
            problems.Row("Класс без классного руководителя", name, "", "");
        }
        foreach (var s in r.UnreachableStudents)
        {
            problems.Row("Ученик без телефона (ни у него, ни у родителей)", s.ClassName, s.Name, "");
        }
        foreach (var t in r.TeachersWithoutPhone)
        {
            problems.Row("Учитель без телефона", "", t, "");
        }
        foreach (var a in r.UnknownPhoneAttempts)
        {
            problems.Row("Вход с неизвестного номера", "", a.Phone,
                $"попыток: {a.Count}, последняя {a.LastAttempt:dd.MM.yyyy H:mm}" + (a.NowKnown ? " — номер уже добавлен" : ""));
        }
        problems.Finish();

        var classes = new SheetWriter(book, "Сводка по классам", title, subtitle, generatedAt);
        classes.Header("Класс", "Классный руководитель", "Учеников", "С телефоном", "Ученики в MAX", "Родителей", "Родители в MAX");
        foreach (var c in r.Classes)
        {
            classes.Row(c.ClassName, c.ClassTeacher ?? "не назначен", c.Students, c.StudentsWithPhone, c.StudentsRegistered,
                c.Parents, c.ParentsRegistered);
        }
        classes.Total("Всего", "", r.Classes.Sum(c => c.Students), r.Classes.Sum(c => c.StudentsWithPhone),
            r.Classes.Sum(c => c.StudentsRegistered), r.Classes.Sum(c => c.Parents), r.Classes.Sum(c => c.ParentsRegistered));
        classes.Finish();
        return Save(book);
    }

    // ---------------------------------------------------------------- Общее

    private static string KindName(ReportLessonKind kind) => kind switch
    {
        ReportLessonKind.Individual => "Индивидуальные",
        ReportLessonKind.Group => "Групповые",
        _ => "Без учеников",
    };

    private static string StatusName(LessonStatus status) => status switch
    {
        LessonStatus.Held => "Проведено",
        LessonStatus.NotHeld => "Не состоялось",
        LessonStatus.Cancelled => "Отменено",
        _ => "Запланировано",
    };

    private static byte[] Save(XLWorkbook book)
    {
        using var stream = new MemoryStream();
        book.SaveAs(stream);
        return stream.ToArray();
    }

    /// <summary>Лист отчёта: шапка (название, период, когда выгружено), заголовки, строки, итог, примечание.</summary>
    private sealed class SheetWriter
    {
        private readonly IXLWorksheet _sheet;
        private int _row;
        private int _headerRow;
        private int _columns;

        public SheetWriter(XLWorkbook book, string name, string title, string subtitle, DateTime generatedAt)
        {
            _sheet = book.Worksheets.Add(name);
            _sheet.Cell(1, 1).Value = title;
            _sheet.Cell(1, 1).Style.Font.SetBold().Font.SetFontSize(14);
            _sheet.Cell(2, 1).Value = subtitle;
            _sheet.Cell(3, 1).Value = $"Выгружено {generatedAt:dd.MM.yyyy H:mm}";
            _sheet.Cell(3, 1).Style.Font.SetItalic().Font.SetFontColor(XLColor.Gray);
            _row = 5;
        }

        public void Header(params string[] names)
        {
            _headerRow = _row;
            _columns = names.Length;
            for (var i = 0; i < names.Length; i++)
            {
                _sheet.Cell(_row, i + 1).Value = names[i];
            }
            var range = _sheet.Range(_row, 1, _row, names.Length);
            range.Style.Font.SetBold().Fill.SetBackgroundColor(XLColor.FromHtml("#DCE3F0"))
                .Border.SetBottomBorder(XLBorderStyleValues.Thin).Alignment.SetWrapText();
            _row++;
        }

        public void Row(params object?[] values) => Write(values, bold: false);

        public void Total(params object?[] values) => Write(values, bold: true);

        public void Note(string text)
        {
            _row++;
            _sheet.Cell(_row, 1).Value = text;
            _sheet.Cell(_row, 1).Style.Font.SetItalic().Font.SetFontColor(XLColor.Gray);
            _row++;
        }

        public void Finish()
        {
            // Ширина — по содержимому, но не по длинной шапке и примечаниям (первые строки и последняя).
            _sheet.Columns(1, Math.Max(_columns, 1)).AdjustToContents(_headerRow, _row);
            foreach (var column in _sheet.Columns(1, Math.Max(_columns, 1)))
            {
                column.Width = Math.Min(Math.Max(column.Width, 10), 60);
            }
            if (_headerRow > 0)
            {
                _sheet.SheetView.FreezeRows(_headerRow);
            }
        }

        private void Write(object?[] values, bool bold)
        {
            for (var i = 0; i < values.Length; i++)
            {
                var cell = _sheet.Cell(_row, i + 1);
                switch (values[i])
                {
                    case null:
                        cell.Value = "—";
                        cell.Style.Alignment.SetHorizontal(XLAlignmentHorizontalValues.Right);
                        break;
                    case decimal hours:
                        cell.Value = hours;
                        cell.Style.NumberFormat.Format = "0.00";
                        break;
                    case int number:
                        cell.Value = number;
                        break;
                    case DateOnly date:
                        cell.Value = date.ToDateTime(TimeOnly.MinValue);
                        cell.Style.DateFormat.Format = "dd.MM.yyyy";
                        break;
                    default:
                        cell.Value = values[i]!.ToString();
                        break;
                }
            }
            if (bold)
            {
                _sheet.Range(_row, 1, _row, values.Length).Style.Font.SetBold().Border.SetTopBorder(XLBorderStyleValues.Thin);
            }
            _row++;
        }
    }
}
