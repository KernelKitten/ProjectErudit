using Erudit.Domain;
using Erudit.Domain.Lessons;
using Erudit.Domain.People;
using Erudit.Infrastructure.Persistence;
using Microsoft.EntityFrameworkCore;

namespace Erudit.Infrastructure.Lessons;

/// <summary>Класс в списке выбора отчёта.</summary>
public sealed record ReportClass(int Id, string Name);

/// <summary>
/// Данные отчёта классного руководителя; подсчёт — <see cref="ClassReport"/>. Классный руководитель видит
/// свои классы текущего учебного года, завуч и администратор — все классы текущего года.
/// </summary>
public sealed class ClassReportService(IDbContextFactory<EruditDbContext> dbFactory, TimeProvider time)
{
    /// <param name="isManager">Активная роль — завуч или администратор.</param>
    public async Task<List<ReportClass>> ClassesAsync(int personId, bool isManager, CancellationToken ct = default)
    {
        var today = DateOnly.FromDateTime(SchoolTime.ToSchool(time.GetUtcNow().UtcDateTime));
        await using var db = await dbFactory.CreateDbContextAsync(ct);
        return await db.SchoolClasses
            .Where(c => c.AcademicYear.StartDate <= today && c.AcademicYear.EndDate >= today)
            .Where(c => isManager || c.ClassTeacherId == personId)
            .OrderBy(c => c.Grade).ThenBy(c => c.Letter)
            .Select(c => new ReportClass(c.Id, c.Grade + c.Letter))
            .ToListAsync(ct);
    }

    /// <summary>Может ли человек смотреть отчёт по этому классу (классный руководитель — только по своему).</summary>
    public async Task<bool> CanViewAsync(int classId, int personId, bool isManager, CancellationToken ct = default)
    {
        await using var db = await dbFactory.CreateDbContextAsync(ct);
        return await db.SchoolClasses.AnyAsync(c => c.Id == classId && (isManager || c.ClassTeacherId == personId), ct);
    }

    /// <param name="from">Первый день периода (по школьным часам), включительно.</param>
    /// <param name="to">Последний день периода, включительно.</param>
    public async Task<ClassReport> LoadAsync(int classId, DateOnly from, DateOnly to, CancellationToken ct = default)
    {
        await using var db = await dbFactory.CreateDbContextAsync(ct);

        // Кто был в классе хотя бы день за период; ушёл и вернулся — несколько отрезков у одного ученика.
        var memberships = await db.ClassMemberships
            .Where(m => m.SchoolClassId == classId && m.FromDate <= to && (m.ToDate == null || m.ToDate >= from))
            .Select(m => new { m.StudentId, m.FromDate, m.ToDate, m.Student.LastName, m.Student.FirstName })
            .ToListAsync(ct);
        var byStudent = memberships.ToLookup(m => m.StudentId);
        var students = byStudent.Select(g =>
        {
            var first = g.Min(m => m.FromDate);
            var last = g.Any(m => m.ToDate == null) ? (DateOnly?)null : g.Max(m => m.ToDate);
            var notes = new List<string>();
            if (first > from)
            {
                notes.Add($"с {first:dd.MM}");
            }
            if (last is { } end && end < to)
            {
                notes.Add($"до {end:dd.MM}");
            }
            var m0 = g.First();
            return new ClassReportStudent(g.Key, $"{m0.LastName} {m0.FirstName}", notes.Count == 0 ? null : string.Join(", ", notes));
        }).ToList();

        var ids = students.Select(s => s.Id).ToList();
        var fromUtc = SchoolTime.ToUtc(from, TimeOnly.MinValue);
        var toUtc = SchoolTime.ToUtc(to.AddDays(1), TimeOnly.MinValue);
        var now = time.GetUtcNow().UtcDateTime;
        var enrollments = await db.Enrollments
            .Where(e => ids.Contains(e.StudentId) && e.Status == EnrollmentStatus.Active
                        && e.Lesson.StartsAt >= fromUtc && e.Lesson.StartsAt < toUtc)
            .Select(e => new
            {
                e.StudentId, e.Attendance, e.Lesson.StartsAt, e.Lesson.DurationMinutes, e.Lesson.ActualDurationMinutes, e.Lesson.Status,
                Subject = e.Lesson.Subject.Name, e.Lesson.Teacher.LastName, e.Lesson.Teacher.FirstName, e.Lesson.Teacher.MiddleName,
            })
            .ToListAsync(ct);

        var visits = enrollments
            // Только занятия, пока ученик был в этом классе: перешёл в 7б — его занятия уже в отчёте 7б.
            .Where(e =>
            {
                var date = DateOnly.FromDateTime(SchoolTime.ToSchool(e.StartsAt));
                return byStudent[e.StudentId].Any(m => m.FromDate <= date && (m.ToDate == null || m.ToDate >= date));
            })
            .Select(e => new ClassReportVisit(e.StudentId, e.Subject, Person.Short(e.LastName, e.FirstName, e.MiddleName), e.Status,
                e.StartsAt.AddMinutes(e.DurationMinutes) <= now, e.Attendance, e.ActualDurationMinutes ?? e.DurationMinutes));

        return ClassReport.Build(students, visits);
    }
}
