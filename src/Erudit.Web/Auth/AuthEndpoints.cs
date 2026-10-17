using Erudit.Infrastructure.Auth;
using Microsoft.AspNetCore.Authentication;
using Microsoft.AspNetCore.Authentication.Cookies;
using Microsoft.AspNetCore.Mvc;

namespace Erudit.Web.Auth;

/// <summary>
/// HTTP-адреса входа и выхода. Это не Blazor-страницы: cookie можно выставить только
/// в обычном HTTP-ответе, а Blazor Server после загрузки общается с сервером через SignalR.
/// Формы на страницах отправляют сюда обычный POST; [FromForm] включает проверку antiforgery-токена.
/// </summary>
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

        auth.MapPost("/switch-role", async ([FromForm] string role, HttpContext http, SignInService signIn) =>
        {
            var home = await signIn.SwitchRoleAsync(http, role);
            return Results.LocalRedirect(home ?? "/");
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
