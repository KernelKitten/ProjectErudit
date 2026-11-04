namespace Erudit.Domain.People;

/// <summary>
/// Любой человек в системе: учитель, ученик, родитель, администратор.
/// Один человек — одна запись, даже если у него несколько ролей.
/// </summary>
public class Person
{
    public int Id { get; set; }

    public string LastName { get; set; } = "";
    public string FirstName { get; set; } = "";
    public string? MiddleName { get; set; }

    /// <summary>Телефон в формате +79991234567. По нему связываем аккаунт MAX с человеком.</summary>
    public string? Phone { get; set; }

    /// <summary>Id пользователя в MAX. Пусто, пока человек не зарегистрировался в боте.</summary>
    public long? MaxUserId { get; set; }

    /// <summary>Выпустился, уволился — не удаляем (нужен для старых отчётов), а выключаем.</summary>
    public bool IsActive { get; set; } = true;

    public List<PersonRole> Roles { get; set; } = [];

    /// <summary>«Иванов Иван Иванович».</summary>
    public string FullName => string.Join(' ', new[] { LastName, FirstName, MiddleName }
        .Where(s => !string.IsNullOrWhiteSpace(s)));

    /// <summary>«Иванов И.И.» — как в школьном расписании.</summary>
    public string ShortName => Short(LastName, FirstName, MiddleName);

    /// <summary>«Иванов И.И.» из частей имени — когда из базы выбраны только они, без всего человека.</summary>
    public static string Short(string lastName, string firstName, string? middleName)
    {
        var initials = string.Concat(new[] { firstName, middleName }
            .Where(s => !string.IsNullOrWhiteSpace(s))
            .Select(s => s![0] + "."));
        return initials.Length == 0 ? lastName : $"{lastName} {initials}";
    }

    public bool HasRole(Role role) => Roles.Any(r => r.Role == role);
}
