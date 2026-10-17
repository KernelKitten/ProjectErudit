using System.Net.Http.Json;
using System.Text.Json;
using System.Text.Json.Nodes;

namespace Erudit.Infrastructure.Max;

/// <summary>
/// Клиент Bot API мессенджера MAX: тонкая обёртка над HTTP-запросами.
/// Адрес и токен в HttpClient подставляются при регистрации (см. DependencyInjection).
/// </summary>
public sealed class MaxBotClient(HttpClient http)
{
    // MAX отвечает в snake_case: user_id, first_name...
    private static readonly JsonSerializerOptions Json = new(JsonSerializerDefaults.Web)
    {
        PropertyNamingPolicy = JsonNamingPolicy.SnakeCaseLower,
    };

    /// <summary>GET /me — информация о боте. Удобно для проверки токена.</summary>
    public async Task<MaxBotInfo> GetMeAsync(CancellationToken ct = default)
    {
        var info = await http.GetFromJsonAsync<MaxBotInfo>("me", Json, ct);
        return info ?? throw new InvalidOperationException("MAX вернул пустой ответ на /me.");
    }

    /// <summary>
    /// GET /updates — long polling: сервер держит запрос открытым до <paramref name="timeoutSeconds"/> секунд,
    /// пока не появятся новые события.
    /// </summary>
    /// <param name="marker">Метка из прошлого ответа: всё до неё считается прочитанным.</param>
    public async Task<MaxUpdatesPage> GetUpdatesAsync(long? marker, int timeoutSeconds, CancellationToken ct = default)
    {
        var url = $"updates?timeout={timeoutSeconds}" + (marker is null ? "" : $"&marker={marker}");
        var root = await http.GetFromJsonAsync<JsonObject>(url, ct)
                   ?? throw new InvalidOperationException("MAX вернул пустой ответ на /updates.");

        // События разбираем как JsonObject (дерево JSON без строгих классов):
        // документация MAX неполная, поэтому сначала смотрим на реальные данные.
        var updates = root["updates"]?.AsArray()
            .OfType<JsonObject>()
            .ToList() ?? [];
        var nextMarker = root["marker"]?.GetValue<long?>();
        return new MaxUpdatesPage(updates, nextMarker);
    }

    /// <summary>POST /messages?user_id=... — отправить сообщение пользователю в личный диалог.</summary>
    public async Task SendMessageToUserAsync(long userId, string text, CancellationToken ct = default)
    {
        using var response = await http.PostAsJsonAsync($"messages?user_id={userId}", new { text }, Json, ct);
        response.EnsureSuccessStatusCode();
    }
}

public sealed record MaxBotInfo(long UserId, string Name, string? Username);

public sealed record MaxUpdatesPage(IReadOnlyList<JsonObject> Updates, long? Marker);
