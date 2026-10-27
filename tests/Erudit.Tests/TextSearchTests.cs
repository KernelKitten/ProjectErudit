using Erudit.Domain;

namespace Erudit.Tests;

public class TextSearchTests
{
    [Theory]
    [InlineData("Семёнов Пётр", "семенов")]
    [InlineData("Семенов Петр", "СЕМЁН")]
    [InlineData("Иванова Мария Петровна", "мар иван")]
    [InlineData("Иванова Мария", "  ")]
    [InlineData("Иванова Мария", null)]
    public void Находит(string text, string? query)
    {
        Assert.True(TextSearch.Matches(text, query));
    }

    [Theory]
    [InlineData("Иванова Мария", "петров")]
    [InlineData("Иванова Мария", "иванова ольга")]
    public void Не_находит(string text, string query)
    {
        Assert.False(TextSearch.Matches(text, query));
    }
}
