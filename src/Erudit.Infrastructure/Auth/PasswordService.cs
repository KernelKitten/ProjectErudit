using System.Security.Cryptography;
using System.Text.Json;
using Erudit.Domain.Accounting;
using Erudit.Domain.Notifications;
using Erudit.Domain.People;
using Erudit.Infrastructure.Persistence;
using Microsoft.AspNetCore.Identity;
using Microsoft.EntityFrameworkCore;

namespace Erudit.Infrastructure.Auth;

public enum PasswordLoginStatus
{
    Ok,
    Invalid, // неверный номер или пароль — не уточняем, что именно (иначе можно перебором узнать, чей номер есть в базе)
    Locked,  // слишком много неверных попыток подряд
}

public sealed record PasswordLoginResult(PasswordLoginStatus Status, int PersonId = 0, bool MustChange = false, DateTime? LockedUntil = null);

/// <summary>
/// Запасной вход по телефону и паролю (решено 2026-11-13). Только учителя, завуч и администратор.
/// Пароль задаёт сам человек после входа через MAX или выдаёт администратор временный (его просим сменить при входе).
/// 5 неверных попыток подряд — вход по паролю для этого номера закрыт на 15 минут. В журнал пишем события без секретов;
/// о смене пароля человеку приходит сообщение в MAX — если это сделал не он, он заметит.
/// </summary>
public sealed class PasswordService(EruditDbContext db, TimeProvider time)
{
    // PBKDF2 с солью и 100 000 повторов (формат ASP.NET Core Identity v3): из хэша пароль не восстановить.
    private static readonly PasswordHasher<PersonPassword> Hasher = new();

    /// <summary>Хэш «ни для кого» — проверяем и его, когда человека нет: так ответ занимает одинаковое время.</summary>
    private static readonly string DummyHash = Hasher.HashPassword(new PersonPassword(), "dummy-password");

    // Буквы и цифры без похожих (0/O, 1/l/I) — временный пароль диктуют по телефону.
    private const string TempAlphabet = "abcdefghjkmnpqrstuvwxyz23456789";

    private DateTime Now => time.GetUtcNow().UtcDateTime;

    public async Task<PasswordLoginResult> LoginAsync(string? phoneInput, string? password, CancellationToken ct = default)
    {
        var phone = PhoneNumber.Normalize(phoneInput);
        var person = phone is null ? null : await db.People.Include(p => p.Roles)
            .SingleOrDefaultAsync(p => p.Phone == phone && p.IsActive, ct);
        var stored = person is null || !PersonPassword.Allowed(person.Roles.Select(r => r.Role)) ? null
            : await db.PersonPasswords.SingleOrDefaultAsync(p => p.PersonId == person.Id, ct);
        if (stored is null)
        {
            Hasher.VerifyHashedPassword(new PersonPassword(), DummyHash, password ?? "");
            return new PasswordLoginResult(PasswordLoginStatus.Invalid);
        }

        var now = Now;
        if (stored.LockedUntil > now)
        {
            return new PasswordLoginResult(PasswordLoginStatus.Locked, LockedUntil: stored.LockedUntil);
        }

        var check = Hasher.VerifyHashedPassword(stored, stored.Hash, password ?? "");
        if (check == PasswordVerificationResult.Failed)
        {
            stored.FailedCount++;
            var locked = stored.FailedCount >= PersonPassword.MaxFailures;
            if (locked)
            {
                stored.LockedUntil = now.AddMinutes(PersonPassword.LockMinutes);
                stored.FailedCount = 0;
            }
            Audit(person!.Id, null, locked ? "PasswordLocked" : "PasswordLoginFailed");
            await db.SaveChangesAsync(ct);
            return locked
                ? new PasswordLoginResult(PasswordLoginStatus.Locked, LockedUntil: stored.LockedUntil)
                : new PasswordLoginResult(PasswordLoginStatus.Invalid);
        }

        if (check == PasswordVerificationResult.SuccessRehashNeeded)
        {
            stored.Hash = Hasher.HashPassword(stored, password!); // способ хэширования стал надёжнее — обновляем
        }
        stored.FailedCount = 0;
        stored.LockedUntil = null;
        Audit(person!.Id, person.Id, "PasswordLogin");
        await db.SaveChangesAsync(ct);
        return new PasswordLoginResult(PasswordLoginStatus.Ok, person.Id, stored.MustChange);
    }

    /// <summary>Есть ли у человека пароль и разрешён ли он ему вообще.</summary>
    public async Task<(bool Allowed, bool HasPassword)> StateAsync(int personId, CancellationToken ct = default)
    {
        var roles = await db.PersonRoles.Where(r => r.PersonId == personId).Select(r => r.Role).ToListAsync(ct);
        return (PersonPassword.Allowed(roles), await db.PersonPasswords.AnyAsync(p => p.PersonId == personId, ct));
    }

    /// <summary>Человек задаёт себе пароль (уже вошёл — через MAX или по паролю).</summary>
    /// <exception cref="InvalidOperationException">Пароль не подходит или человеку пароль не положен.</exception>
    public async Task SetOwnAsync(int personId, string? newPassword, CancellationToken ct = default)
    {
        if (PersonPassword.Validate(newPassword) is { } error)
        {
            throw new InvalidOperationException(error);
        }
        var (person, stored) = await LoadForChangeAsync(personId, ct);
        stored.Hash = Hasher.HashPassword(stored, newPassword!);
        stored.MustChange = false;
        Audit(personId, personId, "PasswordSet");
        Notify(person, "🔑 Пароль для входа на сайт изменён. Если это были не вы — войдите через MAX и задайте новый пароль.");
        await db.SaveChangesAsync(ct);
    }

    /// <summary>Администратор выдаёт временный пароль. Возвращает его — показать один раз; в базе только хэш.</summary>
    public async Task<string> IssueTemporaryAsync(int personId, int adminId, CancellationToken ct = default)
    {
        var temp = GenerateTemporary();
        var (person, stored) = await LoadForChangeAsync(personId, ct);
        stored.Hash = Hasher.HashPassword(stored, temp);
        stored.MustChange = true;
        Audit(personId, adminId, "PasswordTempIssued");
        Notify(person, "🔑 Администратор выдал вам временный пароль для входа на сайт. При первом входе его нужно будет сменить.");
        await db.SaveChangesAsync(ct);
        return temp;
    }

    /// <summary>«k7mp-x3qa-9ft2»: 12 случайных знаков (около 59 бит) — подобрать за 5 попыток невозможно.</summary>
    public static string GenerateTemporary()
    {
        var chars = Enumerable.Range(0, 12).Select(_ => TempAlphabet[RandomNumberGenerator.GetInt32(TempAlphabet.Length)]).ToArray();
        return $"{new string(chars, 0, 4)}-{new string(chars, 4, 4)}-{new string(chars, 8, 4)}";
    }

    private async Task<(Person Person, PersonPassword Stored)> LoadForChangeAsync(int personId, CancellationToken ct)
    {
        var person = await db.People.Include(p => p.Roles).SingleOrDefaultAsync(p => p.Id == personId && p.IsActive, ct);
        if (person is null || !PersonPassword.Allowed(person.Roles.Select(r => r.Role)))
        {
            throw new InvalidOperationException("Вход по паролю доступен только учителям, завучу и администратору.");
        }
        var stored = await db.PersonPasswords.SingleOrDefaultAsync(p => p.PersonId == personId, ct);
        if (stored is null)
        {
            stored = new PersonPassword { PersonId = personId };
            db.PersonPasswords.Add(stored);
        }
        stored.ChangedAt = Now;
        stored.FailedCount = 0;
        stored.LockedUntil = null;
        return (person, stored);
    }

    private void Audit(int personId, int? authorId, string action) => db.AuditEntries.Add(new AuditEntry
    {
        At = Now,
        PersonId = authorId,
        EntityType = "Person",
        EntityId = personId.ToString(),
        Action = action,
        Changes = JsonSerializer.Serialize(new { }),
    });

    private void Notify(Person person, string text)
    {
        if (person.MaxUserId is null)
        {
            return; // нет MAX — некуда писать
        }
        db.Notifications.Add(new Notification
        {
            RecipientId = person.Id, Text = text, CreatedAt = Now, NextAttemptAt = Now, ExpiresAt = Now.AddDays(1),
        });
    }
}
