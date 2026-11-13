using Erudit.Infrastructure.Auth;
using Erudit.Infrastructure.Max;
using Microsoft.Extensions.Options;
using Microsoft.AspNetCore.Authentication;
using Microsoft.AspNetCore.Authentication.Cookies;
using Microsoft.AspNetCore.Mvc;

namespace Erudit.Web.Auth;

/// <summary>
/// HTTP-адреса входа и выхода. Это не Blazor-страницы: cookie можно выставить только
/// в обычном HTTP-ответе, а Blazor Server после загрузки общается с сервером через SignalR.
/// Формы на страницах отправляют сюда обычный POST; [FromForm] включает проверку antiforgery-токена.
/// </summary>
/// <summary>JSON от страницы мини-приложения.</summary>
public sealed record WebAppLogin(string InitData);

public sealed record WebAppContact(string InitData, string Phone, string AuthDate, string Hash);

/// <summary>Redirect — куда перейти после входа; NeedContact — человека не знаем, нужен номер телефона.</summary>
public sealed record WebAppLoginResult(string? Redirect, bool NeedContact);

public static class AuthEndpoints
{
    public static void MapAuthEndpoints(this WebApplication app)
    {
        var auth = app.MapGroup("/auth");

        auth.MapPost("/logout", async (HttpContext http) =>
        {
            await http.SignOutAsync(CookieAuthenticationDefaults.AuthenticationScheme);
            return Results.LocalRedirect("/login");
        });

        // Завершение входа по QR-коду: страница входа переходит сюда, когда вход подтвердили в боте.
        // Нужен секрет, который знает только эта страница; запрос одноразовый.
        auth.MapGet("/complete", async (int id, string secret, bool persistent, HttpContext http,
            LoginRequestService loginRequests, SignInService signIn) =>
        {
            var personId = await loginRequests.CompleteAsync(id, secret);
            var home = personId is null ? null : await signIn.SignInAsync(http, personId.Value, persistent);
            return Results.LocalRedirect(home ?? "/login?error=1");
        });

        // Вход из мини-приложения MAX (страница wwwroot/max.html): MAX сам передаёт подписанный initData.
        // Antiforgery здесь не нужен: подделать подписанные MAX данные без токена бота нельзя.
        auth.MapPost("/max-webapp", async (WebAppLogin body, HttpContext http, IOptions<MaxOptions> max,
            TimeProvider time, PersonRegistration registration, SignInService signIn, ILogger<WebAppLogin> logger) =>
        {
            var maxUserId = MaxWebApp.ValidateInitData(body.InitData, max.Value.BotToken, time.GetUtcNow().UtcDateTime);
            if (maxUserId is null)
            {
                logger.LogWarning("Мини-приложение: initData не прошёл проверку (длина {Length})", body.InitData?.Length ?? 0);
                return Results.Unauthorized();
            }

            var person = await registration.FindByMaxIdAsync(maxUserId.Value);
            logger.LogInformation("Мини-приложение: MAX {MaxUserId} → {Person}", maxUserId, person?.ShortName ?? "не зарегистрирован");
            if (person is null)
            {
                return Results.Ok(new WebAppLoginResult(null, NeedContact: true)); // незнакомый — попросим номер
            }
            var home = await signIn.SignInAsync(http, person.Id, persistent: true);
            return Results.Ok(new WebAppLoginResult(home, NeedContact: false));
        }).DisableAntiforgery();

        // Регистрация из мини-приложения: номер из WebApp.requestContact() с подписью MAX.
        auth.MapPost("/max-webapp/contact", async (WebAppContact body, HttpContext http, IOptions<MaxOptions> max,
            TimeProvider time, PersonRegistration registration, SignInService signIn, ILogger<WebAppContact> logger) =>
        {
            var maxUserId = MaxWebApp.ValidateInitData(body.InitData, max.Value.BotToken, time.GetUtcNow().UtcDateTime);
            if (maxUserId is null)
            {
                return Results.Unauthorized();
            }
            if (!MaxWebApp.ValidateContact(body.Phone, body.AuthDate, body.Hash, maxUserId.Value, max.Value.BotToken))
            {
                logger.LogWarning("Мини-приложение: номер не прошёл проверку подписи (MAX {MaxUserId})", maxUserId);
                return Results.BadRequest(new { error = "Не удалось проверить номер." });
            }

            var person = await registration.LinkMaxAccountAsync(body.Phone, maxUserId.Value);
            if (person is null)
            {
                return Results.NotFound(new { error = "Ваш номер не найден в списках школы. Обратитесь к классному руководителю." });
            }
            var home = await signIn.SignInAsync(http, person.Id, persistent: true);
            return Results.Ok(new WebAppLoginResult(home, NeedContact: false));
        }).DisableAntiforgery();

        auth.MapPost("/switch-role", async ([FromForm] string role, HttpContext http, SignInService signIn) =>
        {
            var home = await signIn.SwitchRoleAsync(http, role);
            return Results.LocalRedirect(home ?? "/");
        }).RequireAuthorization();

        // Запасной вход по телефону и паролю (учителя, завуч, администратор). Ошибку показываем на /login без номера
        // в адресе (персональные данные в адрес не кладём): номер придётся ввести ещё раз.
        auth.MapPost("/password-login", async ([FromForm] string? phone, [FromForm] string? password, [FromForm] bool? foreign,
            HttpContext http, PasswordService passwords, SignInService signIn) =>
        {
            var result = await passwords.LoginAsync(phone, password);
            if (result.Status == PasswordLoginStatus.Locked)
            {
                return Results.LocalRedirect("/login?pwd=locked");
            }
            var home = result.Status == PasswordLoginStatus.Ok
                ? await signIn.SignInAsync(http, result.PersonId, persistent: foreign != true, mustChangePassword: result.MustChange)
                : null;
            return Results.LocalRedirect(home ?? "/login?pwd=invalid");
        });

        // Смена своего пароля (страница /account/password). После смены — cookie заново, без отметки «сменить пароль».
        auth.MapPost("/password", async ([FromForm] string? password, [FromForm] string? confirm,
            HttpContext http, PasswordService passwords, SignInService signIn) =>
        {
            if (password != confirm)
            {
                return Results.LocalRedirect("/account/password?error=mismatch");
            }
            try
            {
                await passwords.SetOwnAsync(http.User.GetPersonId() ?? 0, password);
            }
            catch (InvalidOperationException)
            {
                return Results.LocalRedirect("/account/password?error=invalid");
            }
            await signIn.RefreshAsync(http);
            return Results.LocalRedirect("/account/password?done=true");
        }).RequireAuthorization();

        // Вход под любым человеком без MAX — ТОЛЬКО в режиме разработки.
        // На рабочем сервере этот адрес просто не существует.
        if (app.Environment.IsDevelopment())
        {
            auth.MapPost("/dev-login", async ([FromForm] int personId, HttpContext http, SignInService signIn) =>
            {
                var home = await signIn.SignInAsync(http, personId, persistent: true);
                return home is null ? Results.LocalRedirect("/dev/login?error=1") : Results.LocalRedirect(home);
            });
        }
    }
}
