using Erudit.Domain.People;

namespace Erudit.Tests.People;

public class LoginRequestTests
{
    private static readonly DateTime Start = new(2026, 10, 17, 10, 0, 0, DateTimeKind.Utc);

    private static LoginRequest NewRequest() => new() { CreatedAt = Start };

    [Fact]
    public void Подтверждённый_вход_можно_забрать_только_один_раз()
    {
        var request = NewRequest();
        request.Confirm(personId: 7, Start.AddMinutes(1));

        Assert.Equal(7, request.Use(Start.AddMinutes(2)));
        Assert.Null(request.Use(Start.AddMinutes(2)));
    }

    [Fact]
    public void Неподтверждённый_вход_забрать_нельзя()
    {
        Assert.Null(NewRequest().Use(Start.AddMinutes(1)));
    }

    [Fact]
    public void Код_устаревает_через_3_минуты()
    {
        var request = NewRequest();

        Assert.False(request.IsExpired(Start.AddMinutes(3)));
        Assert.True(request.IsExpired(Start.AddMinutes(3).AddSeconds(1)));
        Assert.Throws<InvalidOperationException>(() => request.Confirm(7, Start.AddMinutes(4)));
    }

    [Fact]
    public void После_подтверждения_на_вход_даётся_2_минуты()
    {
        var request = NewRequest();
        request.Confirm(7, Start.AddMinutes(2));

        Assert.Null(request.Use(Start.AddMinutes(4).AddSeconds(1)));
    }

    [Fact]
    public void Отклонённый_вход_нельзя_подтвердить()
    {
        var request = NewRequest();
        request.Reject();

        Assert.Equal(LoginRequestStatus.Rejected, request.Status);
        Assert.Throws<InvalidOperationException>(() => request.Confirm(7, Start));
    }
}
