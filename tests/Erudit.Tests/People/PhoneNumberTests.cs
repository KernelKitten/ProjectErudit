using Erudit.Domain.People;

namespace Erudit.Tests.People;

public class PhoneNumberTests
{
    [Theory]
    [InlineData("+79991234567", "+79991234567")]
    [InlineData("79991234567", "+79991234567")]  // так присылает MAX
    [InlineData("89991234567", "+79991234567")]  // через восьмёрку
    [InlineData("8 (999) 123-45-67", "+79991234567")]
    [InlineData("+7 999 123 45 67", "+79991234567")]
    [InlineData("9991234567", "+79991234567")]   // без кода страны
    public void Разные_записи_одного_номера(string input, string expected)
    {
        Assert.Equal(expected, PhoneNumber.Normalize(input));
    }

    [Theory]
    [InlineData(null)]
    [InlineData("")]
    [InlineData("123")]
    [InlineData("+1 202 555 0100")]  // не российский
    [InlineData("+799912345678")]    // лишняя цифра
    public void Неправильный_номер__null(string? input)
    {
        Assert.Null(PhoneNumber.Normalize(input));
    }
}
