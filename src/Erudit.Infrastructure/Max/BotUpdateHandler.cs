using System.Text.Json;
using System.Text.Json.Nodes;
using Erudit.Domain.Accounting;
using Erudit.Domain.People;
using Erudit.Infrastructure.Auth;
using Erudit.Infrastructure.Persistence;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;

namespace Erudit.Infrastructure.Max;

/// <summary>
/// Что бот делает с событиями MAX: регистрация по номеру телефона и подтверждение входа на сайт.
/// Не зависит от того, как события пришли (long polling или webhook).
/// </summary>
public sealed class BotUpdateHandler(
    EruditDbContext db,
    PersonRegistration registration,
    IMaxBotClient bot,
    IOptions<MaxOptions> options,
    TimeProvider time,
    ILogger<BotUpdateHandler> logger)
{
    private const string ConfirmPayload = "login_yes_";
    private const string RejectPayload = "login_no_";

    private DateTime Now => time.GetUtcNow().UtcDateTime;

    public async Task HandleAsync(JsonObject update, CancellationToken ct = default)
    {
        switch (update["update_type"]?.GetValue<string>())
        {
            // Нажал «Начать» или открыл ссылку https://max.ru/бот?start=...
            case "bot_started":
                await OnStartedAsync(
                    update["user"]!["user_id"]!.GetValue<long>(),
                    update["payload"]?.GetValue<string>(), ct);
                break;

            // Написал сообщение или поделился номером
            case "message_created":
                var message = update["message"]!.AsObject();
                await OnMessageAsync(message["sender"]!["user_id"]!.GetValue<long>(), message, ct);
                break;

            // Нажал кнопку под сообщением бота
            case "message_callback":
                var callback = update["callback"]!;
                await OnCallbackAsync(
                    callback["user"]!["user_id"]!.GetValue<long>(),
                    callback["callback_id"]!.GetValue<string>(),
                    callback["payload"]?.GetValue<string>() ?? "", ct);
                break;
        }
    }

    private async Task OnStartedAsync(long maxUserId, string? payload, CancellationToken ct)
    {
        var person = await FindByMaxIdAsync(maxUserId, ct);

        if (payload?.StartsWith(LoginRequestService.StartPayloadPrefix) == true)
        {
            var token = payload[LoginRequestService.StartPayloadPrefix.Length..];
            var request = await db.LoginRequests.SingleOrDefaultAsync(r => r.Token == token, ct);
            if (request is null || request.Status != LoginRequestStatus.Pending || request.IsExpired(Now))
            {
                await SendAsync(maxUserId, "Этот QR-код уже не действует. Обновите страницу входа на компьютере и отсканируйте новый код.", ct);
                return;
            }

            // Запоминаем, кто отсканировал: если человек сначала пройдёт регистрацию, вернёмся к этому запросу.
            request.MaxUserId = maxUserId;
            await db.SaveChangesAsync(ct);

            if (person is null)
            {
                await AskPhoneAsync(maxUserId, "Чтобы войти, подтвердите свой номер телефона — по нему мы найдём вас в списках школы.", ct);
            }
            else
            {
                await AskConfirmationAsync(person, request, ct);
            }
            return;
        }

        if (person is null)
        {
            await AskPhoneAsync(maxUserId, "Здравствуйте! Это бот записи на дополнительные занятия. Чтобы зарегистрироваться, подтвердите номер телефона.", ct);
        }
        else
        {
            await SendHintAsync(person, ct);
        }
    }

    private async Task OnMessageAsync(long maxUserId, JsonObject message, CancellationToken ct)
    {
        var hasContact = message["body"]?["attachments"]?.AsArray()
            .Any(a => a?["type"]?.GetValue<string>() == "contact") == true;
        if (hasContact)
        {
            await OnContactAsync(maxUserId, message, ct);
            return;
        }

        var person = await FindByMaxIdAsync(maxUserId, ct);
        if (person is null)
        {
            await AskPhoneAsync(maxUserId, "Сначала нужно зарегистрироваться: подтвердите номер телефона.", ct);
        }
        else
        {
            await SendHintAsync(person, ct);
        }
    }

    private async Task OnContactAsync(long maxUserId, JsonObject message, CancellationToken ct)
    {
        var phone = MaxContact.TryGetVerifiedPhone(message, maxUserId, options.Value.BotToken);
        if (phone is null)
        {
            logger.LogWarning("Контакт от MAX {MaxUserId} не прошёл проверку подписи или принадлежит другому человеку", maxUserId);
            await AskPhoneAsync(maxUserId, "Не удалось проверить номер. Нажмите кнопку «Поделиться номером» ниже — пересланные контакты не подходят.", ct);
            return;
        }

        var person = await registration.LinkMaxAccountAsync(phone, maxUserId, ct);
        if (person is null)
        {
            await SendAsync(maxUserId, "Ваш номер не найден в списках школы. Обратитесь к классному руководителю.", ct);
            return;
        }

        await SendAsync(maxUserId, $"Готово, {person.ShortName}! Номер подтверждён, теперь бот вас знает.", ct);

        // Если человек пришёл по QR-коду — продолжаем вход.
        var request = await db.LoginRequests
            .Where(r => r.MaxUserId == maxUserId && r.Status == LoginRequestStatus.Pending)
            .OrderByDescending(r => r.CreatedAt)
            .FirstOrDefaultAsync(ct);
        if (request is not null && !request.IsExpired(Now))
        {
            await AskConfirmationAsync(person, request, ct);
        }
    }

    private async Task OnCallbackAsync(long maxUserId, string callbackId, string payload, CancellationToken ct)
    {
        var confirm = payload.StartsWith(ConfirmPayload);
        if (!confirm && !payload.StartsWith(RejectPayload)
            || !int.TryParse(payload[(confirm ? ConfirmPayload : RejectPayload).Length..], out var requestId))
        {
            return;
        }

        var person = await FindByMaxIdAsync(maxUserId, ct);
        var request = await db.LoginRequests.SingleOrDefaultAsync(r => r.Id == requestId, ct);
        if (person is null || request is null || request.MaxUserId != maxUserId
            || request.Status != LoginRequestStatus.Pending || request.IsExpired(Now))
        {
            await bot.AnswerCallbackAsync(callbackId, new MaxMessage("Этот запрос на вход уже не действует."), ct);
            return;
        }

        if (confirm)
        {
            request.Confirm(person.Id, Now);
            Audit("Person", person.Id.ToString(), "LoginConfirmed", new { device = request.DeviceInfo });
            await db.SaveChangesAsync(ct);
            await bot.AnswerCallbackAsync(callbackId,
                new MaxMessage($"✅ Вход подтверждён ({request.DeviceInfo}). Вернитесь к компьютеру."), ct);
        }
        else
        {
            request.Reject();
            await db.SaveChangesAsync(ct);
            await bot.AnswerCallbackAsync(callbackId,
                new MaxMessage("Вход отклонён. Если это были не вы — ничего делать не нужно, без подтверждения войти нельзя."), ct);
        }
    }

    private Task AskConfirmationAsync(Person person, LoginRequest request, CancellationToken ct) =>
        bot.SendMessageAsync(person.MaxUserId!.Value, new MaxMessage(
            $"Войти на сайт как {person.ShortName}?\nУстройство: {request.DeviceInfo ?? "неизвестно"}",
            [[MaxButton.Callback("Да, войти", ConfirmPayload + request.Id), MaxButton.Callback("Нет", RejectPayload + request.Id)]]), ct);

    private Task AskPhoneAsync(long maxUserId, string text, CancellationToken ct) =>
        bot.SendMessageAsync(maxUserId, new MaxMessage(text, [[MaxButton.RequestContact("📱 Поделиться номером")]]), ct);

    private Task SendHintAsync(Person person, CancellationToken ct) =>
        SendAsync(person.MaxUserId!.Value,
            $"Здравствуйте, {person.ShortName}! Чтобы войти на сайт, откройте страницу входа на компьютере и отсканируйте QR-код.", ct);

    private Task SendAsync(long maxUserId, string text, CancellationToken ct) =>
        bot.SendMessageAsync(maxUserId, new MaxMessage(text), ct);

    private Task<Person?> FindByMaxIdAsync(long maxUserId, CancellationToken ct) =>
        registration.FindByMaxIdAsync(maxUserId, ct);

    private void Audit(string entityType, string entityId, string action, object changes) =>
        db.AuditEntries.Add(new AuditEntry
        {
            At = Now,
            EntityType = entityType,
            EntityId = entityId,
            Action = action,
            Changes = JsonSerializer.Serialize(changes),
        });
}
