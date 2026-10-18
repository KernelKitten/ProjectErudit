using Erudit.Domain.People;
using Erudit.Infrastructure.Persistence;
using Microsoft.EntityFrameworkCore;

namespace Erudit.Web.Components.Pages.Classes;

/// <summary>Учитель для выпадающего списка «Классный руководитель».</summary>
public sealed record TeacherOption(int Id, string Name)
{
    public override string ToString() => Name;

    /// <summary>Работающие учителя по алфавиту.</summary>
    public static Task<List<TeacherOption>> LoadAsync(EruditDbContext db) =>
        db.People
            .Where(p => p.IsActive && p.Roles.Any(r => r.Role == Role.Teacher))
            .OrderBy(p => p.LastName).ThenBy(p => p.FirstName)
            .Select(p => new TeacherOption(p.Id, p.LastName + " " + p.FirstName + (p.MiddleName == null ? "" : " " + p.MiddleName)))
            .ToListAsync();
}
