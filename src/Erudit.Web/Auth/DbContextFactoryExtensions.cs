using System.Security.Claims;
using Erudit.Infrastructure.Persistence;
using Microsoft.EntityFrameworkCore;

namespace Erudit.Web.Auth;

public static class DbContextFactoryExtensions
{
    /// <summary>
    /// Контекст базы «от имени» пользователя: всё, что он сохранит, попадёт в журнал изменений с его id.
    /// </summary>
    public static async Task<EruditDbContext> CreateForAsync(this IDbContextFactory<EruditDbContext> factory, ClaimsPrincipal user)
    {
        var db = await factory.CreateDbContextAsync();
        db.CurrentPersonId = user.GetPersonId();
        return db;
    }
}
