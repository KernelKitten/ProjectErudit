using Microsoft.JSInterop;

namespace Erudit.Web.Components;

/// <summary>
/// Отдать файл в браузер из Blazor Server. Страница живёт по SignalR, обычного HTTP-ответа у неё нет,
/// поэтому файл передаём потоком (DotNetStreamReference) в функцию erudit.downloadFile (wwwroot/js/erudit.js).
/// </summary>
public static class FileDownload
{
    public const string ExcelType = "application/vnd.openxmlformats-officedocument.spreadsheetml.sheet";

    public static async Task DownloadExcelAsync(this IJSRuntime js, string fileName, byte[] content)
    {
        using var stream = new MemoryStream(content);
        using var streamRef = new DotNetStreamReference(stream);
        await js.InvokeVoidAsync("erudit.downloadFile", fileName, ExcelType, streamRef);
    }
}
