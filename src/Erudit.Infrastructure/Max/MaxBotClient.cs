using System.Net.Http.Json;
using System.Text.Json;
using System.Text.Json.Nodes;

namespace Erudit.Infrastructure.Max;

/// <summary>
/// Что умеет бот. Интерфейс нужен, чтобы в тестах подставить «поддельного» бота,
/// который ничего не отправляет в MAX, а только запоминает сообщения.
/// </summary>
public interface IMaxBotClient
{
    Task<MaxBotInfo> GetMeAsync(CancellationToken ct = default);
    Task<MaxUpdatesPage> GetUpdatesAsync(long? marker, int timeoutSeconds, CancellationToken ct = default);
    Task SendMessageAsync(long userId, MaxMessage message, CancellationToken ct = default);

    /// <summary>Ответ на нажатие кнопки: заменяет сообщение с кнопками новым текстом (кнопки исчезают).</summary>
    Task AnswerCallbackAsync(string callbackId, MaxMessage message, CancellationToken ct = default);
}

/// <summary>
/// Клиент Bot API мессенджера MAX: тонкая обёртка над HTTP-запросами.
/// Адрес и токен в HttpClient подставляются при регистрации (см. DependencyInjection).
/// </summary>
public sealed class MaxBotClient(HttpClient http) : IMaxBotClient
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
        // документация MAX неполная, а у разных событий разные поля.
        var updates = root["updates"]?.AsArray()
            .OfType<JsonObject>()
            .ToList() ?? [];
        var nextMarker = root["marker"]?.GetValue<long?>();
        return new MaxUpdatesPage(updates, nextMarker);
    }

    /// <summary>POST /messages?user_id=... — сообщение пользователю в личный диалог.</summary>
    public async Task SendMessageAsync(long userId, MaxMessage message, CancellationToken ct = default)
    {
        using var response = await http.PostAsJsonAsync($"messages?user_id={userId}", message.ToJson(), ct);
        await EnsureSuccessAsync(response, ct);
    }

    /// <summary>POST /answers?callback_id=... с новым текстом сообщения.</summary>
    public async Task AnswerCallbackAsync(string callbackId, MaxMessage message, CancellationToken ct = default)
    {
        var body = new JsonObject { ["message"] = message.ToJson() };
        using var response = await http.PostAsJsonAsync($"answers?callback_id={Uri.EscapeDataString(callbackId)}", body, ct);
        await EnsureSuccessAsync(response, ct);
    }

    /// <summary>Как EnsureSuccessStatusCode, но с текстом ответа MAX в ошибке — так понятнее, что не так.</summary>
    private static async Task EnsureSuccessAsync(HttpResponseMessage response, CancellationToken ct)
    {
        if (!response.IsSuccessStatusCode)
        {
            var text = await response.Content.ReadAsStringAsync(ct);
            throw new HttpRequestException($"MAX ответил {(int)response.StatusCode}: {text}");
        }
    }
}

public sealed record MaxBotInfo(long UserId, string Name, string? Username);

public sealed record MaxUpdatesPage(IReadOnlyList<JsonObject> Updates, long? Marker);

/// <summary>Кнопка под сообщением.</summary>
public sealed record MaxButton(string Type, string Text, string? Payload = null)
{
    /// <summary>Обычная кнопка: при нажатии бот получает событие message_callback с этим payload.</summary>
    public static MaxButton Callback(string text, string payload) => new("callback", text, payload);

    /// <summary>«Поделиться номером»: MAX пришлёт контакт пользователя с подписью.</summary>
    public static MaxButton RequestContact(string text) => new("request_contact", text);
}

/// <summary>Сообщение бота: текст и, при необходимости, ряды кнопок.</summary>
public sealed record MaxMessage(string Text, IReadOnlyList<IReadOnlyList<MaxButton>>? Buttons = null)
{
    public JsonObject ToJson()
    {
        var json = new JsonObject { ["text"] = Text };
        if (Buttons is { Count: > 0 })
        {
            var rows = new JsonArray();
            foreach (var row in Buttons)
            {
                var buttons = new JsonArray();
                foreach (var b in row)
                {
                    var button = new JsonObject { ["type"] = b.Type, ["text"] = b.Text };
                    if (b.Payload is not null)
                    {
                        button["payload"] = b.Payload;
                    }
                    buttons.Add(button);
                }
                rows.Add(buttons);
            }
            json["attachments"] = new JsonArray
            {
                new JsonObject { ["type"] = "inline_keyboard", ["payload"] = new JsonObject { ["buttons"] = rows } },
            };
        }
        else
        {
            // Пустой список убирает старые кнопки, когда сообщение заменяется (ответ на callback).
            json["attachments"] = new JsonArray();
        }
        return json;
    }
}
