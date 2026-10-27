using System.Text.Json.Nodes;
using Erudit.Domain.People;
using Erudit.Infrastructure.Auth;
using Erudit.Infrastructure.Max;
using Erudit.Infrastructure.Persistence;
using Erudit.Tests.Database;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;

namespace Erudit.Tests.Max;

/// <summary>
/// Весь сценарий входа по QR-коду на настоящей базе, но с «поддельным» ботом:
/// он ничего не отправляет в MAX, а запоминает сообщения, чтобы тест их проверил.
/// </summary>
[Collection(DatabaseCollection.Name)]
public class BotLoginTests(TestDatabase database)
{
    private const string BotToken = "test-bot-token";
    private readonly FakeBot _bot = new();

    [DatabaseFact]
    public async Task Новый_пользователь__регистрация_по_телефону__подтверждение__вход()
    {
        const long maxUserId = 900_000_001;
        var person = await PersonWithPhoneAsync("Белова");
        var service = NewLoginRequestService();
        var (request, secret) = await service.CreateAsync("Windows, Chrome");

        // 1. Отсканировал QR — бот его не знает и просит номер.
        await HandleAsync(Started(maxUserId, LoginRequestService.StartPayloadPrefix + request.Token));
        Assert.Equal("request_contact", _bot.Sent.Last().Message.Buttons![0][0].Type);

        // 2. Поделился номером — бот нашёл человека и спрашивает «Войти?».
        await HandleAsync(ContactShared(maxUserId, person.Phone!));
        var confirmButton = _bot.Sent.Last().Message.Buttons![0][0];
        Assert.Equal($"login_yes_{request.Id}", confirmButton.Payload);

        // 3. Нажал «Да, войти».
        await HandleAsync(Callback(maxUserId, confirmButton.Payload!));
        Assert.Contains("подтверждён", _bot.Answers.Last().Text);
        Assert.Equal(LoginRequestState.Confirmed, await service.GetStateAsync(request.Id));

        // 4. Браузер забирает вход: без секрета — нельзя, с секретом — один раз.
        Assert.Null(await service.CompleteAsync(request.Id, "чужой-секрет"));
        Assert.Equal(person.Id, await service.CompleteAsync(request.Id, secret));
        Assert.Null(await service.CompleteAsync(request.Id, secret));

        await using var db = database.CreateContext();
        Assert.Equal(maxUserId, (await db.People.SingleAsync(p => p.Id == person.Id)).MaxUserId);
    }

    [DatabaseFact]
    public async Task Номер_не_найден__сообщение_и_запись_в_журнале()
    {
        const long maxUserId = 900_000_002;

        await HandleAsync(ContactShared(maxUserId, "79990000000"));

        Assert.Contains("не найден", _bot.Sent.Last().Message.Text);
        await using var db = database.CreateContext();
        Assert.True(await db.AuditEntries.AnyAsync(e =>
            e.Action == "PhoneNotFound" && e.EntityId == maxUserId.ToString()));
    }

    [DatabaseFact]
    public async Task Подтвердить_чужой_запрос_нельзя()
    {
        const long owner = 900_000_003, stranger = 900_000_004;
        var person = await PersonWithPhoneAsync("Орлов");
        await HandleAsync(ContactShared(owner, person.Phone!)); // Орлов зарегистрирован
        var service = NewLoginRequestService();
        var (request, _) = await service.CreateAsync("Windows, Chrome");
        await HandleAsync(Started(owner, LoginRequestService.StartPayloadPrefix + request.Token));

        // Кто-то другой пытается нажать «Да» с подставленным номером запроса.
        await HandleAsync(Callback(stranger, $"login_yes_{request.Id}"));

        Assert.Equal(LoginRequestState.Waiting, await service.GetStateAsync(request.Id));
    }

    // ---------- помощники ----------

    private async Task HandleAsync(JsonObject update)
    {
        await using var db = database.CreateContext();
        var handler = new BotUpdateHandler(db, new PersonRegistration(db, TimeProvider.System), _bot,
            Options.Create(new MaxOptions { BotToken = BotToken }),
            TimeProvider.System, NullLogger<BotUpdateHandler>.Instance);
        await handler.HandleAsync(update);
    }

    private LoginRequestService NewLoginRequestService() => new(database.CreateFactory(), TimeProvider.System);

    private async Task<Person> PersonWithPhoneAsync(string lastName)
    {
        await using var db = database.CreateContext();
        return await db.People.FirstAsync(p => p.LastName == lastName && p.Phone != null);
    }

    private static JsonObject Started(long userId, string payload) => new()
    {
        ["update_type"] = "bot_started",
        ["user"] = new JsonObject { ["user_id"] = userId },
        ["payload"] = payload,
    };

    private static JsonObject ContactShared(long userId, string phone)
    {
        var message = MaxContactTests.ContactMessage(phone.TrimStart('+'), userId, BotToken);
        message["sender"] = new JsonObject { ["user_id"] = userId };
        return new JsonObject { ["update_type"] = "message_created", ["message"] = message };
    }

    private static JsonObject Callback(long userId, string payload) => new()
    {
        ["update_type"] = "message_callback",
        ["callback"] = new JsonObject
        {
            ["callback_id"] = Guid.NewGuid().ToString(),
            ["payload"] = payload,
            ["user"] = new JsonObject { ["user_id"] = userId },
        },
    };

    private sealed class FakeBot : IMaxBotClient
    {
        public List<(long UserId, MaxMessage Message)> Sent { get; } = [];
        public List<MaxMessage> Answers { get; } = [];

        public Task<MaxBotInfo> GetMeAsync(CancellationToken ct = default) => Task.FromResult(new MaxBotInfo(1, "Тестовый бот", "test_bot"));
        public Task<MaxUpdatesPage> GetUpdatesAsync(long? marker, int timeoutSeconds, CancellationToken ct = default) =>
            Task.FromResult(new MaxUpdatesPage([], marker));

        public Task SendMessageAsync(long userId, MaxMessage message, CancellationToken ct = default)
        {
            Sent.Add((userId, message));
            return Task.CompletedTask;
        }

        public Task AnswerCallbackAsync(string callbackId, MaxMessage message, CancellationToken ct = default)
        {
            Answers.Add(message);
            return Task.CompletedTask;
        }
    }
}
