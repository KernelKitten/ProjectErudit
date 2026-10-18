using Erudit.Domain.People;
using Erudit.Domain.School;
using Erudit.Infrastructure.Import;
using Erudit.Infrastructure.Persistence;
using Microsoft.EntityFrameworkCore;

namespace Erudit.Infrastructure.People;

/// <summary>
/// Ученики класса и их родители: загрузка из Excel в пустой класс, добавление и правка, «Выбыл», перевод.
/// Правила (согласованы 2026-10-18):
///  - один человек — один телефон; номер родителя уже есть с тем же ФИО → это тот же родитель (братья, сёстры);
///  - родитель без телефона не создаётся (одно ФИО бесполезно) — предупреждение;
///  - «Все уведомления о ребёнке» по умолчанию у 0–4 классов;
///  - хоть одна ошибка в файле — не загружаем ничего.
/// </summary>
public sealed class StudentService(EruditDbContext db)
{
    public static readonly IReadOnlyList<ExcelColumn> Columns =
    [
        new("name", "ФИО ученика", Required: true, "фиоученика", "фио", "ученик", "учащийся", "ребенок", "фиоребенка"),
        new("phone", "Телефон ученика", Required: false, "телефонученика", "телефон", "тел", "мобильный", "телефонребенка"),
        new("parent1", "Родитель 1", Required: false, "родитель1", "фиородителя1", "родитель", "фиородителя", "мама", "мать", "фиомамы", "фиоматери"),
        new("parent1phone", "Телефон родителя 1", Required: false, "телефонродителя1", "телродителя1", "телефонродителя", "телефонмамы", "телефонматери", "телмамы"),
        new("parent2", "Родитель 2", Required: false, "родитель2", "фиородителя2", "папа", "отец", "фиопапы", "фиоотца"),
        new("parent2phone", "Телефон родителя 2", Required: false, "телефонродителя2", "телродителя2", "телефонпапы", "телефонотца", "телпапы"),
    ];

    /// <summary>С какого дня ученик числится в классе: сегодня, но не раньше начала учебного года.</summary>
    private static DateOnly JoinDate(SchoolClass schoolClass, DateOnly today) =>
        today < schoolClass.AcademicYear.StartDate ? schoolClass.AcademicYear.StartDate : today;

    // ---------------------------------------------------------------- Загрузка из Excel

    public async Task<StudentImportPreview> PreviewAsync(int classId, Stream xlsx, CancellationToken ct = default)
    {
        var table = ExcelTableReader.Read(xlsx, Columns);
        var rows = new List<StudentImportRow>();

        foreach (var r in table.Rows)
        {
            var row = new StudentImportRow(r.RowNumber, r.Get("name"), r.Get("phone"));
            row.Name = PersonName.TryParse(row.RawName);
            if (row.Name is null)
            {
                row.Errors.Add("ФИО ученика: нужны хотя бы фамилия и имя");
            }
            row.Phone = ParsePhone(row.RawPhone, "Телефон ученика", row.Errors);

            foreach (var (nameKey, phoneKey, title) in new[] { ("parent1", "parent1phone", "Родитель 1"), ("parent2", "parent2phone", "Родитель 2") })
            {
                var rawName = r.Get(nameKey);
                var rawPhone = r.Get(phoneKey);
                if (rawName.Length == 0 && rawPhone.Length == 0)
                {
                    continue;
                }
                if (rawPhone.Length == 0)
                {
                    row.Warnings.Add($"{title} без телефона — пропущен (войти и получать уведомления не сможет)");
                    continue;
                }
                var parentName = PersonName.TryParse(rawName);
                if (parentName is null)
                {
                    row.Errors.Add($"{title}: укажите ФИО (хотя бы фамилию и имя)");
                    continue;
                }
                var phone = ParsePhone(rawPhone, $"Телефон «{title}»", row.Errors);
                if (phone is not null)
                {
                    row.Parents.Add(new StudentImportParent(parentName, phone));
                }
            }

            if (row.Phone is null && row.Parents.Count == 0 && row.Errors.Count == 0)
            {
                row.Warnings.Add("Нет ни одного телефона — уведомления о занятиях никто не получит");
            }
            rows.Add(row);
        }

        CheckDuplicatesInFile(rows);
        await CheckAgainstDatabaseAsync(rows, ct);
        return new StudentImportPreview(table.SheetName, rows);
    }

    public async Task<int> ApplyAsync(int classId, StudentImportPreview preview, DateOnly today, CancellationToken ct = default)
    {
        if (preview.HasErrors)
        {
            throw new InvalidOperationException("В файле есть ошибки — загрузка невозможна.");
        }
        var schoolClass = await db.SchoolClasses.Include(c => c.AcademicYear).SingleAsync(c => c.Id == classId, ct);
        if (await db.ClassMemberships.AnyAsync(m => m.SchoolClassId == classId && m.ToDate == null, ct))
        {
            throw new InvalidOperationException("В классе уже есть ученики — загрузка из Excel только в пустой класс.");
        }

        var parentsByPhone = new Dictionary<string, Person>(); // один родитель на несколько строк (дети из одного класса)
        foreach (var row in preview.Rows)
        {
            var student = NewPerson(row.Name!, row.Phone, Role.Student);
            db.People.Add(student);
            db.ClassMemberships.Add(new ClassMembership { Student = student, SchoolClass = schoolClass, FromDate = JoinDate(schoolClass, today) });

            foreach (var p in row.Parents)
            {
                if (!parentsByPhone.TryGetValue(p.Phone, out var parent))
                {
                    parent = p.ExistingPersonId is { } id
                        ? await db.People.Include(x => x.Roles).SingleAsync(x => x.Id == id, ct)
                        : db.People.Add(NewPerson(p.Name, p.Phone, Role.Parent)).Entity;
                    EnsureRole(parent, Role.Parent);
                    parentsByPhone[p.Phone] = parent;
                }
                db.ParentLinks.Add(new ParentLink
                {
                    Parent = parent,
                    Student = student,
                    NotifyAll = schoolClass.Grade <= 4,
                    CreatedAt = DateTime.UtcNow,
                });
            }
        }
        await db.SaveChangesAsync(ct); // одна транзакция — всё или ничего
        return preview.Rows.Count;
    }

    private static void CheckDuplicatesInFile(List<StudentImportRow> rows)
    {
        // Телефон ученика должен быть уникальным и не совпадать с телефоном какого-либо родителя.
        var studentPhones = rows.Where(r => r.Phone is not null).GroupBy(r => r.Phone!);
        foreach (var group in studentPhones.Where(g => g.Count() > 1))
        {
            foreach (var row in group)
            {
                row.Errors.Add($"Телефон ученика повторяется в строках {string.Join(", ", group.Select(r => r.RowNumber))}");
            }
        }
        var parentPhones = rows.SelectMany(r => r.Parents.Select(p => (Row: r, Parent: p))).ToList();
        foreach (var row in rows.Where(r => r.Phone is not null && parentPhones.Any(p => p.Parent.Phone == r.Phone)))
        {
            row.Errors.Add("Телефон ученика совпадает с телефоном родителя");
        }
        // Один телефон родителя в нескольких строках — это один человек (дети из одного класса), ФИО должно совпадать.
        foreach (var group in parentPhones.GroupBy(p => p.Parent.Phone).Where(g => g.Count() > 1))
        {
            var first = group.First().Parent.Name;
            foreach (var (row, parent) in group.Where(x => !x.Parent.Name.SameAs(first.LastName, first.FirstName, first.MiddleName)))
            {
                row.Errors.Add($"Телефон {parent.Phone} в файле у разных людей: {first.FullName} и {parent.Name.FullName}");
            }
        }
    }

    private async Task CheckAgainstDatabaseAsync(List<StudentImportRow> rows, CancellationToken ct)
    {
        var phones = rows.Select(r => r.Phone).Concat(rows.SelectMany(r => r.Parents.Select(p => p.Phone)))
            .Where(p => p is not null).Distinct().ToList();
        var owners = await db.People.Include(p => p.Roles).Where(p => phones.Contains(p.Phone)).ToListAsync(ct);

        foreach (var row in rows)
        {
            if (row.Phone is not null && owners.SingleOrDefault(o => o.Phone == row.Phone) is { } owner)
            {
                row.Errors.Add($"Телефон ученика уже есть в базе: {owner.FullName}");
            }
            foreach (var parent in row.Parents)
            {
                if (owners.SingleOrDefault(o => o.Phone == parent.Phone) is not { } existing)
                {
                    continue;
                }
                var problem = CheckParentPhoneOwner(existing, parent.Name);
                if (problem is null)
                {
                    parent.ExistingPersonId = existing.Id;
                    row.Warnings.Add($"{existing.ShortName} уже есть в базе — будет привязан(а) и к этому ребёнку");
                }
                else
                {
                    row.Errors.Add(problem);
                }
            }
        }
    }

    /// <summary>Можно ли считать владельца номера тем же родителем. null — можно.</summary>
    public static string? CheckParentPhoneOwner(Person owner, PersonName name)
    {
        if (owner.HasRole(Role.Student))
        {
            return $"Телефон родителя уже у ученика {owner.ShortName}";
        }
        return name.SameAs(owner.LastName, owner.FirstName, owner.MiddleName)
            ? null
            : $"Телефон родителя уже у другого человека: {owner.FullName}";
    }

    // ---------------------------------------------------------------- Карточка ученика

    /// <summary>
    /// Сохраняет ученика (новый — если studentId пустой) вместе с родителями. Возвращает текст ошибки или null.
    /// Родители, которых нет в списке, отвязываются от ребёнка (сам родитель остаётся в базе).
    /// </summary>
    public async Task<string?> SaveStudentAsync(int classId, int? studentId, StudentInput input, DateOnly today, CancellationToken ct = default)
    {
        var name = PersonName.TryParse(input.FullName);
        if (name is null)
        {
            return "ФИО ученика: нужны хотя бы фамилия и имя";
        }
        var phone = PhoneNumber.Normalize(input.Phone);
        if (!string.IsNullOrWhiteSpace(input.Phone) && phone is null)
        {
            return "Телефон ученика не похож на российский номер";
        }
        if (phone is not null && await db.People.AnyAsync(p => p.Phone == phone && p.Id != studentId, ct))
        {
            var owner = await db.People.SingleAsync(p => p.Phone == phone, ct);
            return $"Телефон ученика уже у: {owner.FullName}";
        }

        var schoolClass = await db.SchoolClasses.Include(c => c.AcademicYear).SingleAsync(c => c.Id == classId, ct);
        Person student;
        if (studentId is null)
        {
            student = db.People.Add(NewPerson(name, phone, Role.Student)).Entity;
            db.ClassMemberships.Add(new ClassMembership { Student = student, SchoolClass = schoolClass, FromDate = JoinDate(schoolClass, today) });
        }
        else
        {
            student = await db.People.SingleAsync(p => p.Id == studentId, ct);
            (student.LastName, student.FirstName, student.MiddleName) = (name.LastName, name.FirstName, name.MiddleName);
            student.Phone = phone;
        }

        var parentPhones = input.Parents.Select(p => PhoneNumber.Normalize(p.Phone)).Where(p => p is not null).ToList();
        if (parentPhones.Count != parentPhones.Distinct().Count())
        {
            return "Один и тот же телефон указан у двух родителей";
        }

        var links = studentId is null ? [] : await db.ParentLinks.Where(l => l.StudentId == studentId).ToListAsync(ct);
        var keptParentIds = new List<int>();
        foreach (var p in input.Parents)
        {
            var parentName = PersonName.TryParse(p.FullName);
            var parentPhone = PhoneNumber.Normalize(p.Phone);
            if (parentName is null || parentPhone is null)
            {
                return "У родителя нужны ФИО (фамилия и имя) и телефон";
            }
            if (parentPhone == phone)
            {
                return "Телефон родителя совпадает с телефоном ученика";
            }

            var owner = await db.People.Include(x => x.Roles).SingleOrDefaultAsync(x => x.Phone == parentPhone, ct);
            // Правим уже привязанного родителя, если телефон у него тот же или новый номер ещё ни у кого не занят.
            var editingSameParent = p.ParentId is not null && (owner is null || owner.Id == p.ParentId);
            Person parent;
            if (editingSameParent)
            {
                parent = await db.People.Include(x => x.Roles).SingleAsync(x => x.Id == p.ParentId, ct);
                (parent.LastName, parent.FirstName, parent.MiddleName) = (parentName.LastName, parentName.FirstName, parentName.MiddleName);
                parent.Phone = parentPhone;
            }
            else if (owner is not null)
            {
                var problem = CheckParentPhoneOwner(owner, parentName);
                if (problem is not null)
                {
                    return problem;
                }
                parent = owner; // уже есть в базе — например, мама старшего брата
            }
            else
            {
                parent = db.People.Add(NewPerson(parentName, parentPhone, Role.Parent)).Entity;
            }
            EnsureRole(parent, Role.Parent);

            var link = links.FirstOrDefault(l => l.ParentId == parent.Id && parent.Id != 0);
            if (link is null)
            {
                db.ParentLinks.Add(link = new ParentLink { Parent = parent, Student = student, CreatedAt = DateTime.UtcNow });
            }
            link.NotifyAll = p.NotifyAll;
            if (parent.Id != 0)
            {
                keptParentIds.Add(parent.Id);
            }
        }

        // Отвязать родителей, которых убрали из карточки (связь удаляется, человек остаётся).
        db.ParentLinks.RemoveRange(links.Where(l => !keptParentIds.Contains(l.ParentId)));

        await db.SaveChangesAsync(ct);
        return null;
    }

    // ---------------------------------------------------------------- Выбыл, перевод

    /// <summary>Ученик выбыл: последний день в классе — lastDay. Выключается, но остаётся в базе для отчётов.</summary>
    public async Task<string?> LeaveAsync(int studentId, int classId, DateOnly lastDay, CancellationToken ct = default)
    {
        var membership = await CurrentMembershipAsync(studentId, classId, ct);
        if (membership is null)
        {
            return "Ученик уже не в этом классе";
        }
        if (lastDay < membership.FromDate)
        {
            return $"Дата раньше, чем ученик пришёл в класс ({membership.FromDate:dd.MM.yyyy})";
        }
        membership.ToDate = lastDay;
        (await db.People.SingleAsync(p => p.Id == studentId, ct)).IsActive = false;
        await db.SaveChangesAsync(ct);
        return null;
    }

    /// <summary>Перевод в другой класс того же учебного года с даты firstDay (в старом классе — по firstDay − 1).</summary>
    public async Task<string?> TransferAsync(int studentId, int fromClassId, int toClassId, DateOnly firstDay, CancellationToken ct = default)
    {
        var membership = await CurrentMembershipAsync(studentId, fromClassId, ct);
        if (membership is null)
        {
            return "Ученик уже не в этом классе";
        }
        if (fromClassId == toClassId)
        {
            return "Выберите другой класс";
        }
        if (firstDay <= membership.FromDate)
        {
            return $"Дата перевода должна быть позже {membership.FromDate:dd.MM.yyyy} — дня, когда ученик пришёл в класс";
        }
        var from = await db.SchoolClasses.SingleAsync(c => c.Id == fromClassId, ct);
        var to = await db.SchoolClasses.SingleAsync(c => c.Id == toClassId, ct);
        if (from.AcademicYearId != to.AcademicYearId)
        {
            return "Перевести можно только в класс того же учебного года";
        }

        membership.ToDate = firstDay.AddDays(-1);
        db.ClassMemberships.Add(new ClassMembership { StudentId = studentId, SchoolClassId = toClassId, FromDate = firstDay });
        await db.SaveChangesAsync(ct);
        return null;
    }

    private Task<ClassMembership?> CurrentMembershipAsync(int studentId, int classId, CancellationToken ct) =>
        db.ClassMemberships.SingleOrDefaultAsync(m => m.StudentId == studentId && m.SchoolClassId == classId && m.ToDate == null, ct);

    // ---------------------------------------------------------------- Общее

    private static string? ParsePhone(string raw, string title, List<string> errors)
    {
        if (raw.Length == 0)
        {
            return null;
        }
        var phone = PhoneNumber.Normalize(raw);
        if (phone is null)
        {
            errors.Add($"{title}: «{raw}» не похож на российский номер");
        }
        return phone;
    }

    private static Person NewPerson(PersonName name, string? phone, Role role) => new()
    {
        LastName = name.LastName,
        FirstName = name.FirstName,
        MiddleName = name.MiddleName,
        Phone = phone,
        Roles = [new PersonRole { Role = role }],
    };

    private static void EnsureRole(Person person, Role role)
    {
        if (!person.HasRole(role))
        {
            person.Roles.Add(new PersonRole { Role = role });
        }
    }
}

public sealed record StudentInput(string FullName, string? Phone, IReadOnlyList<ParentInput> Parents);

/// <summary>Родитель в карточке ученика. ParentId — если он уже привязан (правка), пусто — новый.</summary>
public sealed record ParentInput(int? ParentId, string FullName, string Phone, bool NotifyAll);

public sealed record StudentImportPreview(string SheetName, IReadOnlyList<StudentImportRow> Rows)
{
    public bool HasErrors => Rows.Count == 0 || Rows.Any(r => r.Errors.Count > 0);
}

public sealed class StudentImportRow(int rowNumber, string rawName, string rawPhone)
{
    public int RowNumber { get; } = rowNumber;
    public string RawName { get; } = rawName;
    public string RawPhone { get; } = rawPhone;
    public PersonName? Name { get; set; }
    public string? Phone { get; set; }
    public List<StudentImportParent> Parents { get; } = [];
    public List<string> Errors { get; } = [];
    public List<string> Warnings { get; } = [];
}

public sealed class StudentImportParent(PersonName name, string phone)
{
    public PersonName Name { get; } = name;
    public string Phone { get; } = phone;

    /// <summary>Уже есть в базе (например, родитель старшего брата) — будет привязан к ребёнку.</summary>
    public int? ExistingPersonId { get; set; }
}
