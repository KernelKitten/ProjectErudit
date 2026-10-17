using System.Security.Claims;

namespace Erudit.Web.Auth;

/// <summary>
/// Что лежит в cookie входа («утверждения», claims):
/// - NameIdentifier — id человека (Person.Id);
/// - Name — «Иванов И.И.»;
/// - Role — ОДНА активная роль: по ней работает [Authorize(Roles = ...)];
/// - erudit:available_role — все роли, между которыми можно переключаться.
/// </summary>
public static class EruditClaims
{
    public const string AvailableRole = "erudit:available_role";
}

public static class ClaimsPrincipalExtensions
{
    public static int? GetPersonId(this ClaimsPrincipal user) =>
        int.TryParse(user.FindFirstValue(ClaimTypes.NameIdentifier), out var id) ? id : null;

    public static string? GetActiveRole(this ClaimsPrincipal user) => user.FindFirstValue(ClaimTypes.Role);

    public static IReadOnlyList<string> GetAvailableRoles(this ClaimsPrincipal user) =>
        user.FindAll(EruditClaims.AvailableRole).Select(c => c.Value).ToList();
}
