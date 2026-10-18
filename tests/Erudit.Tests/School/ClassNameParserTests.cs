using Erudit.Domain.School;

namespace Erudit.Tests.School;

public class ClassNameParserTests
{
    private static string Names(ClassNameParseResult r) => string.Join(" ", r.Classes.Select(c => $"{c.Grade}{c.Letter}"));

    [Fact]
    public void Список_через_запятую_с_диапазоном()
    {
        var result = ClassNameParser.Parse("1а, 1б, 2а, 5а-в, 10, 11");

        Assert.Empty(result.Errors);
        Assert.Equal("1а 1б 2а 5а 5б 5в 10 11", Names(result));
    }

    [Theory]
    [InlineData("5А-В")]   // заглавные
    [InlineData("5а–в")]   // длинное тире из Word
    [InlineData("5а 5б;5в")] // пробелы и точка с запятой
    public void Разные_записи(string input)
    {
        Assert.Equal("5а 5б 5в", Names(ClassNameParser.Parse(input)));
    }

    [Fact]
    public void Повторы_убираются()
    {
        Assert.Equal("7а 7б", Names(ClassNameParser.Parse("7а, 7а-б, 7б")));
    }

    [Fact]
    public void Ошибки_собираются_все()
    {
        var result = ClassNameParser.Parse("5а, 12б, abc, 7в-а, 0а");

        Assert.Equal("0а 5а", Names(result)); // правильные всё равно разобраны
        Assert.Equal(3, result.Errors.Count);
        Assert.Contains(result.Errors, e => e.Contains("12б"));
        Assert.Contains(result.Errors, e => e.Contains("abc"));
        Assert.Contains(result.Errors, e => e.Contains("7в-а"));
    }
}
