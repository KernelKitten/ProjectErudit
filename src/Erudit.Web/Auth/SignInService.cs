using System.Security.Claims;
using Erudit.Domain;
using Erudit.Infrastructure.Persistence;
using Microsoft.AspNetCore.Authentication;
using Microsoft.AspNetCore.Authentication.Cookies;
using Microsoft.EntityFrameworkCore;

namespace Erudit.Web.Auth;

/// <summary>
/// Выдаёт cookie входа. Один и тот же код для всех способов входа:
/// вход разработчика, QR-код через бота, мини-приложение MAX.
/// </summary>
public sealed class SignInService(IDbContextFactory<EruditDbContext> dbFactory, TimeProvider time)
{
    /// <summary>
    /// Входит под человеком <paramref name="personId"/>. Возвращает стартовую страницу активной роли
    /// или null, если человек не найден, выключен или у него нет ни одной роли.
    /// </summary>
    /// <param name="persistent">true — вход на 30 дней; false — до закрытия браузера («чужой компьютер»).</param>
    /// <param name="activeRole">Какую роль сделать активной; если не задана или недоступна — первая по приоритету.</param>
    public async Task<string?> SignInAsync(HttpContext http, int personId, bool persistent, string? activeRole = null)
    {
        var principal = await BuildPrincipalAsync(personId, activeRole);
        if (principal is null)
        {
            return null;
        }

        await http.SignInAsync(CookieAuthenticationDefaults.AuthenticationScheme, principal,
            new AuthenticationProperties { IsPersistent = persistent });
        return AppRoles.HomePage(principal.GetActiveRole()!);
    }

    /// <summary>
    /// Переключает активную роль, не выходя из системы. Сохраняет «запомнить меня» прежнего входа.
    /// </summary>
    public async Task<string?> SwitchRoleAsync(HttpContext http, string role)
    {
        var personId = http.User.GetPersonId();
        if (personId is null || !http.User.GetAvailableRoles().Contains(role))
        {
            return null;
        }
        var current = await http.AuthenticateAsync(CookieAuthenticationDefaults.AuthenticationScheme);
        var persistent = current.Properties?.IsPersistent ?? false;
        return await SignInAsync(http, personId.Value, persistent, role);
    }

    private async Task<ClaimsPrincipal?> BuildPrincipalAsync(int personId, string? activeRole)
    {
        await using var db = await dbFactory.CreateDbContextAsync();

        var person = await db.People
            .Include(p => p.Roles)
            .SingleOrDefaultAsync(p => p.Id == personId && p.IsActive);
        if (person is null)
        {
            return null;
        }

        var roles = person.Roles.Select(r => AppRoles.FromDomain(r.Role)).ToHashSet();

        // Классный руководитель — если ведёт класс в текущем учебном году.
        var today = DateOnly.FromDateTime(SchoolTime.ToSchool(time.GetUtcNow().UtcDateTime));
        var leadsClass = await db.SchoolClasses.AnyAsync(c => c.ClassTeacherId == personId
            && c.AcademicYear.StartDate <= today && c.AcademicYear.EndDate >= today);
        if (leadsClass)
        {
            roles.Add(AppRoles.ClassTeacher);
        }

        var available = AppRoles.ByPriority.Where(roles.Contains).ToList();
        if (available.Count == 0)
        {
            return null;
        }
        var active = activeRole is not null && available.Contains(activeRole) ? activeRole : available[0];

        var claims = new List<Claim>
        {
            new(ClaimTypes.NameIdentifier, person.Id.ToString()),
            new(ClaimTypes.Name, person.ShortName),
            new(ClaimTypes.Role, active),
        };
        claims.AddRange(available.Select(r => new Claim(EruditClaims.AvailableRole, r)));

        var identity = new ClaimsIdentity(claims, CookieAuthenticationDefaults.AuthenticationScheme);
        return new ClaimsPrincipal(identity);
    }
}
