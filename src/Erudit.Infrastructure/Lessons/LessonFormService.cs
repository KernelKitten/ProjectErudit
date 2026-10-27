using Erudit.Domain;
using Erudit.Domain.People;
using Erudit.Domain.School;
using Erudit.Infrastructure.Persistence;
using Microsoft.EntityFrameworkCore;

namespace Erudit.Infrastructure.Lessons;

/// <summary>Строка выпадающего списка: предмет, кабинет, класс.</summary>
public sealed record FormOption(int Id, string Name);

/// <summary>Ученик для выбора: «Абрамов Артём», класс на дату занятия (может не быть).</summary>
public sealed record StudentOption(int Id, string Name, string? ClassName)
{
    public string Label => ClassName is null ? Name : $"{Name}, {ClassName}";
}

/// <summary>Всё, что нужно окну «Новое занятие» сразу при открытии.</summary>
/// <param name="LastSubjectId">Предмет последнего занятия учителя — подставляем по умолчанию.</param>
/// <param name="LastRoomId">Кабинет последнего занятия учителя.</param>
public sealed record LessonFormData(
    IReadOnlyList<LessonPeriod> Periods,
    IReadOnlyList<FormOption> Subjects,
    IReadOnlyList<FormOption> Rooms,
    IReadOnlyList<FormOption> Classes,
    int? LastSubjectId,
    int? LastRoomId);

/// <summary>Данные для окна «Новое занятие»: справочники, классы и ученики.</summary>
public sealed class LessonFormService(IDbContextFactory<EruditDbContext> dbFactory)
{
    /// <summary>Сколько учеников показывать в поиске по школе.</summary>
    public const int SearchLimit = 20;

    /// <param name="date">Дата занятия: от неё зависит учебный год (какие классы показывать).</param>
    public async Task<LessonFormData> LoadAsync(int teacherId, DateOnly date, CancellationToken ct = default)
    {
        await using var db = await dbFactory.CreateDbContextAsync(ct);

        var periods = await db.LessonPeriods.OrderBy(p => p.Number).ToListAsync(ct);
        var subjects = await db.Subjects.Where(s => s.IsActive).OrderBy(s => s.Name)
            .Select(s => new FormOption(s.Id, s.Name)).ToListAsync(ct);
        var rooms = await db.Rooms.Where(r => r.IsActive).OrderBy(r => r.Name)
            .Select(r => new FormOption(r.Id, r.Name)).ToListAsync(ct);
        var classes = (await db.SchoolClasses
                .Where(c => c.AcademicYear.StartDate <= date && c.AcademicYear.EndDate >= date)
                .OrderBy(c => c.Grade).ThenBy(c => c.Letter)
                .ToListAsync(ct))
            .Select(c => new FormOption(c.Id, c.Name))
            .ToList();

        // «Как в прошлый раз»: последнее созданное занятие учителя (больший Id — созданное позже).
        var last = await db.Lessons.Where(l => l.TeacherId == teacherId)
            .OrderByDescending(l => l.Id)
            .Select(l => new { l.SubjectId, l.RoomId })
            .FirstOrDefaultAsync(ct);

        return new LessonFormData(periods, subjects, rooms, classes,
            subjects.Any(s => s.Id == last?.SubjectId) ? last!.SubjectId : null,
            rooms.Any(r => r.Id == last?.RoomId) ? last!.RoomId : null);
    }

    /// <summary>Ученики класса на дату занятия, по алфавиту.</summary>
    public async Task<IReadOnlyList<StudentOption>> StudentsOfClassAsync(int classId, DateOnly date, CancellationToken ct = default)
    {
        await using var db = await dbFactory.CreateDbContextAsync(ct);
        var rows = await db.ClassMemberships
            .Where(m => m.SchoolClassId == classId && m.FromDate <= date && (m.ToDate == null || m.ToDate >= date)
                        && m.Student.IsActive)
            .OrderBy(m => m.Student.LastName).ThenBy(m => m.Student.FirstName)
            .Select(m => new { m.StudentId, m.Student.LastName, m.Student.FirstName, m.SchoolClass.Grade, m.SchoolClass.Letter })
            .ToListAsync(ct);
        return rows.Select(r => new StudentOption(r.StudentId, $"{r.LastName} {r.FirstName}", $"{r.Grade}{r.Letter}")).ToList();
    }

    /// <summary>
    /// Поиск ученика по всей школе: без учёта регистра и «е/ё» (<see cref="TextSearch"/>).
    /// Учеников до тысячи — проще отфильтровать в памяти, чем учить базу «ё».
    /// </summary>
    public async Task<IReadOnlyList<StudentOption>> SearchStudentsAsync(string? query, DateOnly date, CancellationToken ct = default)
    {
        if (string.IsNullOrWhiteSpace(query))
        {
            return [];
        }
        await using var db = await dbFactory.CreateDbContextAsync(ct);

        var students = await db.People
            .Where(p => p.IsActive && p.Roles.Any(r => r.Role == Role.Student))
            .OrderBy(p => p.LastName).ThenBy(p => p.FirstName)
            .Select(p => new { p.Id, p.LastName, p.FirstName })
            .ToListAsync(ct);
        var found = students.Where(s => TextSearch.Matches($"{s.LastName} {s.FirstName}", query)).Take(SearchLimit).ToList();

        var ids = found.Select(s => s.Id).ToList();
        var classes = (await db.ClassMemberships
                .Where(m => ids.Contains(m.StudentId) && m.FromDate <= date && (m.ToDate == null || m.ToDate >= date))
                .Select(m => new { m.StudentId, m.SchoolClass.Grade, m.SchoolClass.Letter })
                .ToListAsync(ct))
            .DistinctBy(m => m.StudentId)
            .ToDictionary(m => m.StudentId, m => $"{m.Grade}{m.Letter}");

        return found.Select(s => new StudentOption(s.Id, $"{s.LastName} {s.FirstName}", classes.GetValueOrDefault(s.Id)))
            .ToList();
    }
}
