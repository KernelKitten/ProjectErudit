using Erudit.Domain.People;

namespace Erudit.Tests.People;

public class PersonNameTests
{
    [Theory]
    [InlineData("Иванова Мария Петровна", "Иванова", "Мария", "Петровна")]
    [InlineData("  иВАНОВА   мария  ", "Иванова", "Мария", null)]          // лишние пробелы, регистр, без отчества
    [InlineData("петров-водкин кузьма сергеевич", "Петров-Водкин", "Кузьма", "Сергеевич")]
    [InlineData("Алиева Лейла Рашид кызы", "Алиева", "Лейла", "Рашид кызы")]
    public void Разбор_ФИО(string text, string last, string first, string? middle)
    {
        Assert.Equal(new PersonName(last, first, middle), PersonName.TryParse(text));
    }

    [Theory]
    [InlineData(null)]
    [InlineData("")]
    [InlineData("Иванова")] // только фамилия
    public void Без_имени__null(string? text)
    {
        Assert.Null(PersonName.TryParse(text));
    }

    [Fact]
    public void Сравнение_без_учёта_регистра_и_ё()
    {
        var name = PersonName.TryParse("Ершова Наталья Ивановна")!;

        Assert.True(name.SameAs("ЕРШОВА", "наталья", "ивановна"));
        Assert.True(PersonName.TryParse("Сёмина Анна")!.SameAs("Семина", "Анна", null));
        Assert.False(name.SameAs("Ершова", "Наталья", null));
    }
}
