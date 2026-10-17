using Erudit.Web.Auth;

namespace Erudit.Tests.Web;

public class DeviceDescriptionTests
{
    [Theory]
    [InlineData("Mozilla/5.0 (Windows NT 10.0; Win64; x64) AppleWebKit/537.36 (KHTML, like Gecko) Chrome/141.0.0.0 Safari/537.36",
        "Windows, Chrome")]
    [InlineData("Mozilla/5.0 (Windows NT 10.0; Win64; x64) AppleWebKit/537.36 (KHTML, like Gecko) Chrome/141.0.0.0 Safari/537.36 Edg/141.0.0.0",
        "Windows, Edge")]
    [InlineData("Mozilla/5.0 (Windows NT 10.0; Win64; x64) AppleWebKit/537.36 (KHTML, like Gecko) Chrome/138.0.0.0 YaBrowser/25.8.0.0 Safari/537.36",
        "Windows, Яндекс Браузер")]
    [InlineData("Mozilla/5.0 (Macintosh; Intel Mac OS X 10_15_7) AppleWebKit/605.1.15 (KHTML, like Gecko) Version/18.0 Safari/605.1.15",
        "macOS, Safari")]
    [InlineData("Mozilla/5.0 (Linux; Android 14; SM-A546B) AppleWebKit/537.36 (KHTML, like Gecko) Chrome/141.0.0.0 Mobile Safari/537.36",
        "Android, Chrome")]
    [InlineData("Mozilla/5.0 (X11; Ubuntu; Linux x86_64; rv:131.0) Gecko/20100101 Firefox/131.0",
        "Linux, Firefox")]
    [InlineData(null, "неизвестное устройство")]
    public void Описание_устройства(string? userAgent, string expected)
    {
        Assert.Equal(expected, DeviceDescription.FromUserAgent(userAgent));
    }
}
