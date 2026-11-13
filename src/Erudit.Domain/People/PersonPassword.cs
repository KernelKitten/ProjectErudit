namespace Erudit.Domain.People;

/// <summary>
/// Пароль для запасного входа (решено 2026-11-13): только у учителей, завуча и администратора, логин — телефон.
/// Отдельная таблица, а не поля в <see cref="Person"/>: хэш не попадает в журнал изменений вместе с правкой карточки
/// (в журнал пишем только явные события без секретов). Сам пароль не храним — только хэш (PBKDF2 с солью).
/// </summary>
public class PersonPassword
{
    public const int MinLength = 8;
    public const int MaxLength = 128;

    /// <summary>Столько неверных попыток подряд — и вход по паролю закрыт на <see cref="LockMinutes"/>.</summary>
    public const int MaxFailures = 5;
    public const int LockMinutes = 15;

    public int PersonId { get; set; }
    public Person Person { get; set; } = null!;

    public string Hash { get; set; } = "";

    /// <summary>Временный пароль от администратора: при входе сразу попросим сменить.</summary>
    public bool MustChange { get; set; }

    public DateTime ChangedAt { get; set; }

    public int FailedCount { get; set; }
    public DateTime? LockedUntil { get; set; }

    /// <summary>Кому можно завести пароль: учителю, завучу, администратору. Ученики и родители входят через MAX.</summary>
    public static bool Allowed(IEnumerable<Role> roles) => roles.Any(r => r is Role.Teacher or Role.HeadTeacher or Role.Admin);

    /// <summary>Ошибка в новом пароле или null.</summary>
    public static string? Validate(string? password) =>
        string.IsNullOrEmpty(password) || password.Length < MinLength ? $"Пароль должен быть не короче {MinLength} символов."
        : password.Length > MaxLength ? $"Пароль должен быть не длиннее {MaxLength} символов."
        : string.IsNullOrWhiteSpace(password) ? "Пароль не может состоять из одних пробелов."
        : null;
}
