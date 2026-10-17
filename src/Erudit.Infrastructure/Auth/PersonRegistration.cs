using System.Text.Json;
using Erudit.Domain.Accounting;
using Erudit.Domain.People;
using Erudit.Infrastructure.Persistence;
using Microsoft.EntityFrameworkCore;

namespace Erudit.Infrastructure.Auth;

/// <summary>
/// Регистрация: проверенный номер телефона → человек из списков школы → привязка аккаунта MAX.
/// Одна и та же логика для бота (кнопка «Поделиться номером») и мини-приложения (WebApp.requestContact).
/// </summary>
public sealed class PersonRegistration(EruditDbContext db, TimeProvider time)
{
    /// <summary>
    /// Привязывает аккаунт MAX к человеку с этим номером. Возвращает человека или null, если номера нет в списках
    /// (такая попытка записывается в журнал, чтобы администратор её увидел).
    /// </summary>
    public async Task<Person?> LinkMaxAccountAsync(string verifiedPhone, long maxUserId, CancellationToken ct = default)
    {
        var phone = PhoneNumber.Normalize(verifiedPhone);
        var person = phone is null ? null
            : await db.People.Include(p => p.Roles).SingleOrDefaultAsync(p => p.Phone == phone && p.IsActive, ct);
        if (person is null)
        {
            Audit("Registration", maxUserId.ToString(), "PhoneNotFound", new { phone = phone ?? verifiedPhone });
            await db.SaveChangesAsync(ct);
            return null;
        }

        // Один аккаунт MAX — один человек: если этот аккаунт был у кого-то другого, отвязываем.
        var previous = await db.People.Where(p => p.MaxUserId == maxUserId && p.Id != person.Id).ToListAsync(ct);
        previous.ForEach(p => p.MaxUserId = null);
        await db.SaveChangesAsync(ct);

        if (person.MaxUserId != maxUserId)
        {
            person.MaxUserId = maxUserId;
            Audit("Person", person.Id.ToString(), "MaxLinked", new { maxUserId });
            await db.SaveChangesAsync(ct);
        }
        return person;
    }

    public Task<Person?> FindByMaxIdAsync(long maxUserId, CancellationToken ct = default) =>
        db.People.Include(p => p.Roles).SingleOrDefaultAsync(p => p.MaxUserId == maxUserId && p.IsActive, ct);

    private void Audit(string entityType, string entityId, string action, object changes) =>
        db.AuditEntries.Add(new AuditEntry
        {
            At = time.GetUtcNow().UtcDateTime,
            EntityType = entityType,
            EntityId = entityId,
            Action = action,
            Changes = JsonSerializer.Serialize(changes),
        });
}
