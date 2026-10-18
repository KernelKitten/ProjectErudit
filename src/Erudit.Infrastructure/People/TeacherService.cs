using Erudit.Domain.People;
using Erudit.Infrastructure.Import;
using Erudit.Infrastructure.Persistence;
using Microsoft.EntityFrameworkCore;

namespace Erudit.Infrastructure.People;

/// <summary>
/// Учителя: загрузка из Excel (предпросмотр → загрузка) и общие правила для ручного добавления.
/// Правила:
///  - ФИО обязательно, одной строкой; телефон необязателен (без него — предупреждение: не сможет войти);
///  - один человек — один телефон: если номер уже у родителя с тем же ФИО — добавляем ему роль «Учитель»,
///    если ФИО другое или это ученик/учитель — ошибка;
///  - хоть одна ошибка в файле — не загружаем ничего.
/// </summary>
public sealed class TeacherService(EruditDbContext db)
{
    public static readonly IReadOnlyList<ExcelColumn> Columns =
    [
        new("name", "ФИО", Required: true, "фио", "фамилияимяотчество", "учитель", "преподаватель", "сотрудник"),
        new("phone", "Телефон", Required: false, "телефон", "тел", "мобильный", "сотовый", "номертелефона", "номер"),
    ];

    /// <summary>Есть ли уже учителя (Excel — только для пустого списка).</summary>
    public Task<bool> AnyTeachersAsync(CancellationToken ct = default) =>
        db.People.AnyAsync(p => p.Roles.Any(r => r.Role == Role.Teacher), ct);

    /// <summary>Читает файл и проверяет каждую строку. Ничего не сохраняет.</summary>
    public async Task<TeacherImportPreview> PreviewAsync(Stream xlsx, CancellationToken ct = default)
    {
        var table = ExcelTableReader.Read(xlsx, Columns);
        var rows = table.Rows.Select(r => new TeacherImportRow(r.RowNumber, r.Get("name"), r.Get("phone"))).ToList();

        foreach (var row in rows)
        {
            row.Name = PersonName.TryParse(row.RawName);
            if (row.Name is null)
            {
                row.Errors.Add("ФИО: нужны хотя бы фамилия и имя");
            }

            if (row.RawPhone.Length == 0)
            {
                row.Warnings.Add("Нет телефона — не сможет войти, пока номер не добавят");
            }
            else
            {
                row.Phone = PhoneNumber.Normalize(row.RawPhone);
                if (row.Phone is null)
                {
                    row.Errors.Add($"Телефон «{row.RawPhone}» не похож на российский номер");
                }
            }
        }

        // Повторы внутри файла.
        foreach (var group in rows.Where(r => r.Phone is not null).GroupBy(r => r.Phone).Where(g => g.Count() > 1))
        {
            foreach (var row in group)
            {
                row.Errors.Add($"Телефон повторяется в строках {string.Join(", ", group.Select(r => r.RowNumber))}");
            }
        }
        foreach (var group in rows.Where(r => r.Name is not null).GroupBy(r => r.Name!.FullName.ToLowerInvariant()).Where(g => g.Count() > 1))
        {
            foreach (var row in group)
            {
                row.Warnings.Add($"ФИО повторяется в строках {string.Join(", ", group.Select(r => r.RowNumber))}");
            }
        }

        // Телефоны, которые уже есть в базе.
        var phones = rows.Where(r => r.Phone is not null).Select(r => r.Phone!).ToList();
        var owners = await db.People.Include(p => p.Roles).Where(p => p.Phone != null && phones.Contains(p.Phone)).ToListAsync(ct);
        foreach (var row in rows.Where(r => r.Phone is not null && r.Name is not null))
        {
            var owner = owners.SingleOrDefault(p => p.Phone == row.Phone);
            if (owner is not null)
            {
                var error = CheckPhoneOwner(owner, row.Name!);
                if (error is null)
                {
                    row.ExistingPersonId = owner.Id;
                    row.Warnings.Add($"{owner.ShortName} уже есть в базе ({RoleNames(owner)}) — станет и учителем");
                }
                else
                {
                    row.Errors.Add(error);
                }
            }
        }

        return new TeacherImportPreview(table.SheetName, rows);
    }

    /// <summary>Загружает строки предпросмотра. Только если ошибок нет; одна транзакция — всё или ничего.</summary>
    public async Task<int> ApplyAsync(TeacherImportPreview preview, CancellationToken ct = default)
    {
        if (preview.HasErrors)
        {
            throw new InvalidOperationException("В файле есть ошибки — загрузка невозможна.");
        }

        foreach (var row in preview.Rows)
        {
            if (row.ExistingPersonId is { } id)
            {
                var person = await db.People.Include(p => p.Roles).SingleAsync(p => p.Id == id, ct);
                person.Roles.Add(new PersonRole { Role = Role.Teacher });
            }
            else
            {
                db.People.Add(new Person
                {
                    LastName = row.Name!.LastName,
                    FirstName = row.Name.FirstName,
                    MiddleName = row.Name.MiddleName,
                    Phone = row.Phone,
                    Roles = [new PersonRole { Role = Role.Teacher }],
                });
            }
        }
        await db.SaveChangesAsync(ct); // SaveChanges сам работает в транзакции
        return preview.Rows.Count;
    }

    /// <summary>
    /// Можно ли «этому» человеку с таким же номером стать учителем. null — можно (это тот же человек).
    /// </summary>
    public static string? CheckPhoneOwner(Person owner, PersonName name)
    {
        if (owner.HasRole(Role.Teacher))
        {
            return $"Номер уже у учителя {owner.ShortName}";
        }
        if (owner.HasRole(Role.Student))
        {
            return $"Номер уже у ученика {owner.ShortName}";
        }
        if (!name.SameAs(owner.LastName, owner.FirstName, owner.MiddleName))
        {
            return $"Номер уже у другого человека: {owner.FullName}";
        }
        return null;
    }

    public static string RoleNames(Person person) => string.Join(", ", person.Roles.Select(r => r.Role switch
    {
        Role.Admin => "администратор",
        Role.HeadTeacher => "завуч",
        Role.Teacher => "учитель",
        Role.Student => "ученик",
        Role.Parent => "родитель",
        _ => r.Role.ToString(),
    }));
}

public sealed record TeacherImportPreview(string SheetName, IReadOnlyList<TeacherImportRow> Rows)
{
    public bool HasErrors => Rows.Any(r => r.Errors.Count > 0) || Rows.Count == 0;
}

public sealed class TeacherImportRow(int rowNumber, string rawName, string rawPhone)
{
    public int RowNumber { get; } = rowNumber;
    public string RawName { get; } = rawName;
    public string RawPhone { get; } = rawPhone;
    public PersonName? Name { get; set; }
    public string? Phone { get; set; }

    /// <summary>Уже есть в базе (например, родитель) — ему добавится роль «Учитель».</summary>
    public int? ExistingPersonId { get; set; }

    public List<string> Errors { get; } = [];
    public List<string> Warnings { get; } = [];
}
