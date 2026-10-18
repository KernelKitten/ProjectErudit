using System.Text;
using ClosedXML.Excel;

namespace Erudit.Infrastructure.Import;

/// <summary>
/// Колонка, которую мы ищем в Excel. Synonyms — как её могут назвать люди («ФИО», «Ф.И.О.», «ФИО учителя»...).
/// </summary>
public sealed record ExcelColumn(string Key, string Title, bool Required, params string[] Synonyms);

public sealed record ExcelRow(int RowNumber, IReadOnlyDictionary<string, string> Values)
{
    public string Get(string key) => Values.GetValueOrDefault(key) ?? "";
}

public sealed record ExcelTable(string SheetName, int HeaderRow, IReadOnlyList<ExcelRow> Rows);

/// <summary>
/// Читает таблицу из .xlsx «как получится» — учителя заполняют файлы по-разному:
/// - лист любой: берём первый, где нашлись все обязательные колонки;
/// - строка заголовков любая из первых 15 (сверху бывает название, дата, пустые строки);
/// - колонки узнаём по названию с вариантами, без учёта регистра, пробелов, точек и «ё»;
/// - лишние колонки и пустые строки пропускаем.
/// Проблемы с самим файлом → FormatException с понятным текстом.
/// </summary>
public static class ExcelTableReader
{
    private const int HeaderSearchRows = 15;

    public static ExcelTable Read(Stream xlsx, IReadOnlyList<ExcelColumn> columns)
    {
        XLWorkbook workbook;
        try
        {
            workbook = new XLWorkbook(xlsx);
        }
        catch (Exception ex)
        {
            // Исходную ошибку сохраняем внутри (InnerException) — человеку она не нужна, а разработчику в логе пригодится.
            throw new FormatException(
                "Не получилось открыть файл. Нужна «Книга Excel (.xlsx)»: откройте файл в Excel и выберите «Сохранить как» → «Книга Excel».", ex);
        }

        using (workbook)
        {
            (IXLWorksheet Sheet, int Row, Dictionary<string, int> Map)? best = null;

            foreach (var sheet in workbook.Worksheets)
            {
                var lastRow = Math.Min(sheet.LastRowUsed()?.RowNumber() ?? 0, HeaderSearchRows);
                for (var r = 1; r <= lastRow; r++)
                {
                    var map = MatchHeader(sheet.Row(r), columns);
                    if (best is null || map.Count > best.Value.Map.Count)
                    {
                        best = (sheet, r, map);
                    }
                    if (columns.Where(c => c.Required).All(c => map.ContainsKey(c.Key)))
                    {
                        return new ExcelTable(sheet.Name, r, ReadRows(sheet, r, map));
                    }
                }
            }

            var found = best?.Map.Keys.ToHashSet() ?? [];
            var missing = columns.Where(c => c.Required && !found.Contains(c.Key)).Select(c => $"«{c.Title}»");
            throw new FormatException($"Не нашлась колонка {string.Join(", ", missing)}. Проверьте, что в первой строке таблицы есть заголовки.");
        }
    }

    /// <summary>Какие из нужных колонок есть в этой строке: ключ колонки → номер столбца.</summary>
    private static Dictionary<string, int> MatchHeader(IXLRow row, IReadOnlyList<ExcelColumn> columns)
    {
        var map = new Dictionary<string, int>();
        foreach (var cell in row.CellsUsed())
        {
            var header = Normalize(cell.GetFormattedString());
            if (header.Length == 0)
            {
                continue;
            }
            // Заголовок совпадает с вариантом или начинается с него: «ФИО учителя» → «фио».
            // Именно «начинается», а не «содержит»: иначе «тел» нашлось бы в слове «учиТЕЛя».
            // Из подходящих колонок берём ту, чей вариант ДЛИННЕЕ (точнее): «Телефон мамы» подходит и под
            // «телефон» (ученика), и под «телефонмамы» — выигрывает «телефонмамы».
            var best = columns
                .Where(c => !map.ContainsKey(c.Key))
                .Select(c => (Column: c, Length: c.Synonyms.Select(Normalize)
                    .Where(s => header == s || header.StartsWith(s))
                    .Select(s => s.Length)
                    .DefaultIfEmpty(0)
                    .Max()))
                .Where(x => x.Length > 0)
                .OrderByDescending(x => x.Length)
                .FirstOrDefault();
            if (best.Column is not null)
            {
                map[best.Column.Key] = cell.Address.ColumnNumber;
            }
        }
        return map;
    }

    private static List<ExcelRow> ReadRows(IXLWorksheet sheet, int headerRow, Dictionary<string, int> map)
    {
        var rows = new List<ExcelRow>();
        var lastRow = sheet.LastRowUsed()?.RowNumber() ?? headerRow;
        for (var r = headerRow + 1; r <= lastRow; r++)
        {
            // GetFormattedString — текст «как видно в Excel»: телефон-число 79991234567 не превратится в 7,99E+10.
            var values = map.ToDictionary(m => m.Key, m => sheet.Cell(r, m.Value).GetFormattedString().Trim());
            if (values.Values.All(string.IsNullOrWhiteSpace))
            {
                continue; // пустые строки между записями — не ошибка
            }
            rows.Add(new ExcelRow(r, values));
        }
        return rows;
    }

    /// <summary>«Ф.И.О. учителя» → «фиоучителя»: только буквы и цифры, маленькие, «ё» → «е».</summary>
    private static string Normalize(string text)
    {
        var sb = new StringBuilder(text.Length);
        foreach (var ch in text.ToLowerInvariant())
        {
            if (char.IsLetterOrDigit(ch))
            {
                sb.Append(ch == 'ё' ? 'е' : ch);
            }
        }
        return sb.ToString();
    }
}
