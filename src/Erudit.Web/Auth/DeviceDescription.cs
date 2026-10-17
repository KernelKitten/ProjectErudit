namespace Erudit.Web.Auth;

/// <summary>
/// Короткое описание устройства по строке User-Agent браузера: «Windows, Chrome».
/// Показываем его в боте, чтобы человек узнал своё устройство и не подтвердил чужой вход.
/// Порядок проверок важен: в строке Edge есть и «Chrome», и «Edg/».
/// </summary>
public static class DeviceDescription
{
    public static string FromUserAgent(string? userAgent)
    {
        if (string.IsNullOrWhiteSpace(userAgent))
        {
            return "неизвестное устройство";
        }

        var os =
            userAgent.Contains("Android") ? "Android" :
            userAgent.Contains("iPhone") || userAgent.Contains("iPad") ? "iOS" :
            userAgent.Contains("Windows") ? "Windows" :
            userAgent.Contains("Mac OS") ? "macOS" :
            userAgent.Contains("Linux") ? "Linux" :
            "другая система";

        var browser =
            userAgent.Contains("YaBrowser") ? "Яндекс Браузер" :
            userAgent.Contains("Edg/") ? "Edge" :
            userAgent.Contains("OPR/") ? "Opera" :
            userAgent.Contains("Firefox") ? "Firefox" :
            userAgent.Contains("Chrome") ? "Chrome" :
            userAgent.Contains("Safari") ? "Safari" :
            "браузер";

        return $"{os}, {browser}";
    }
}
