using Erudit.Domain.People;

namespace Erudit.Tests.People;

public class PersonTests
{
    [Fact]
    public void Краткое_имя_как_в_расписании()
    {
        var person = new Person { LastName = "Кульбашный", FirstName = "Виктор", MiddleName = "Васильевич" };

        Assert.Equal("Кульбашный В.В.", person.ShortName);
        Assert.Equal("Кульбашный Виктор Васильевич", person.FullName);
    }

    [Fact]
    public void Без_отчества()
    {
        var person = new Person { LastName = "Smith", FirstName = "John" };

        Assert.Equal("Smith J.", person.ShortName);
        Assert.Equal("Smith John", person.FullName);
    }
}
