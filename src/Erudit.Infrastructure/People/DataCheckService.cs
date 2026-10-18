using System.Text.Json;
using Erudit.Domain;
using Erudit.Domain.People;
using Erudit.Infrastructure.Persistence;
using Microsoft.EntityFrameworkCore;

namespace Erudit.Infrastructure.People;

/// <summary>
/// Проверочный отчёт «правильно ли загрузили школу»: сводка по классам учебного года
/// и список того, что стоит поправить, прежде чем звать людей в систему.
/// </summary>
public sealed class DataCheckService(EruditDbContext db)
{
    public async Task<DataCheckReport> BuildAsync(int academicYearId, CancellationToken ct = default)
    {
        // Ученики, которые сейчас в классах этого года (ToDate == null).
        var members = await db.ClassMemberships
            .Where(m => m.SchoolClass.AcademicYearId == academicYearId && m.ToDate == null && m.Student.IsActive)
            .Select(m => new
            {
                m.SchoolClassId,
                m.StudentId,
                Name = m.Student.LastName + " " + m.Student.FirstName,
                HasPhone = m.Student.Phone != null,
                Registered = m.Student.MaxUserId != null,
            })
            .ToListAsync(ct);
        var studentIds = members.Select(m => m.StudentId).ToList();

        var parents = await db.ParentLinks
            .Where(l => studentIds.Contains(l.StudentId))
            .Select(l => new { l.StudentId, l.ParentId, HasPhone = l.Parent.Phone != null, Registered = l.Parent.MaxUserId != null })
            .ToListAsync(ct);

        var classes = await db.SchoolClasses
            .Where(c => c.AcademicYearId == academicYearId)
            .OrderBy(c => c.Grade).ThenBy(c => c.Letter)
            .Select(c => new
            {
                c.Id,
                c.Grade,
                c.Letter,
                Teacher = c.ClassTeacher == null ? null : c.ClassTeacher.LastName + " " + c.ClassTeacher.FirstName.Substring(0, 1) + ".",
            })
            .ToListAsync(ct);

        var rows = classes.Select(c =>
        {
            var students = members.Where(m => m.SchoolClassId == c.Id).ToList();
            var ids = students.Select(s => s.StudentId).ToHashSet();
            var classParents = parents.Where(p => ids.Contains(p.StudentId)).DistinctBy(p => p.ParentId).ToList();
            return new DataCheckClassRow(c.Id, $"{c.Grade}{c.Letter}", c.Teacher,
                students.Count, students.Count(s => s.HasPhone), students.Count(s => s.Registered),
                classParents.Count, classParents.Count(p => p.Registered));
        }).ToList();

        var className = classes.ToDictionary(c => c.Id, c => $"{c.Grade}{c.Letter}");

        // Ученик без единого телефона: ни у него, ни у родителей — уведомления о занятиях никто не получит.
        var unreachable = members
            .Where(m => !m.HasPhone && !parents.Any(p => p.StudentId == m.StudentId && p.HasPhone))
            .OrderBy(m => className[m.SchoolClassId]).ThenBy(m => m.Name)
            .Select(m => new DataCheckStudent(m.StudentId, m.Name, m.SchoolClassId, className[m.SchoolClassId]))
            .ToList();

        var teachersWithoutPhone = await db.People
            .Where(p => p.IsActive && p.Phone == null && p.Roles.Any(r => r.Role == Role.Teacher))
            .OrderBy(p => p.LastName).ThenBy(p => p.FirstName)
            .Select(p => p.LastName + " " + p.FirstName + (p.MiddleName == null ? "" : " " + p.MiddleName))
            .ToListAsync(ct);

        return new DataCheckReport(rows, rows.Where(r => r.ClassTeacher is null).Select(r => r.ClassName).ToList(),
            unreachable, teachersWithoutPhone, await UnknownPhoneAttemptsAsync(ct));
    }

    /// <summary>
    /// Попытки войти с номером, которого нет в списках (бот пишет их в журнал как Registration/PhoneNotFound).
    /// Последние 100, по одной на номер; помечаем, если номер с тех пор уже добавили кому-то.
    /// </summary>
    private async Task<List<UnknownPhoneAttempt>> UnknownPhoneAttemptsAsync(CancellationToken ct)
    {
        var entries = await db.AuditEntries
            .Where(e => e.EntityType == "Registration" && e.Action == "PhoneNotFound")
            .OrderByDescending(e => e.At)
            .Take(100)
            .ToListAsync(ct);

        var attempts = entries
            .Select(e => new { e.At, Phone = ReadPhone(e.Changes) })
            .Where(x => x.Phone is not null)
            .GroupBy(x => x.Phone!)
            .Select(g => new { Phone = g.Key, Last = g.Max(x => x.At), Count = g.Count() })
            .ToList();

        var phones = attempts.Select(a => a.Phone).ToList();
        var nowKnown = await db.People.Where(p => p.Phone != null && phones.Contains(p.Phone)).Select(p => p.Phone!).ToListAsync(ct);

        return attempts
            .Select(a => new UnknownPhoneAttempt(a.Phone, SchoolTime.ToSchool(a.Last), a.Count, nowKnown.Contains(a.Phone)))
            .OrderByDescending(a => a.LastAttempt)
            .ToList();
    }

    private static string? ReadPhone(string? changesJson)
    {
        if (changesJson is null)
        {
            return null;
        }
        using var doc = JsonDocument.Parse(changesJson);
        return doc.RootElement.TryGetProperty("phone", out var phone) ? phone.GetString() : null;
    }
}

public sealed record DataCheckReport(
    IReadOnlyList<DataCheckClassRow> Classes,
    IReadOnlyList<string> ClassesWithoutTeacher,
    IReadOnlyList<DataCheckStudent> UnreachableStudents,
    IReadOnlyList<string> TeachersWithoutPhone,
    IReadOnlyList<UnknownPhoneAttempt> UnknownPhoneAttempts);

public sealed record DataCheckClassRow(int ClassId, string ClassName, string? ClassTeacher,
    int Students, int StudentsWithPhone, int StudentsRegistered, int Parents, int ParentsRegistered);

public sealed record DataCheckStudent(int StudentId, string Name, int ClassId, string ClassName);

/// <summary>LastAttempt — по школьному времени. NowKnown — номер уже есть у кого-то в базе (поправили).</summary>
public sealed record UnknownPhoneAttempt(string Phone, DateTime LastAttempt, int Count, bool NowKnown);
