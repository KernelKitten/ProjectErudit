using Erudit.Domain.People;

namespace Erudit.Web.Auth;

/// <summary>
/// Роли, между которыми переключается пользователь в интерфейсе.
/// Это роли из базы (Role) плюс «классный руководитель», который вычисляется по классам.
/// Используются строками в [Authorize(Roles = AppRoles.Teacher)].
/// </summary>
public static class AppRoles
{
    public const string Teacher = "Teacher";
    public const string ClassTeacher = "ClassTeacher";
    public const string HeadTeacher = "HeadTeacher";
    public const string Admin = "Admin";
    public const string Parent = "Parent";
    public const string Student = "Student";

    /// <summary>Порядок важен: при входе активной становится первая доступная роль из этого списка.</summary>
    public static readonly IReadOnlyList<string> ByPriority = [Teacher, ClassTeacher, HeadTeacher, Admin, Parent, Student];

    public static string FromDomain(Role role) => role switch
    {
        Role.Admin => Admin,
        Role.HeadTeacher => HeadTeacher,
        Role.Teacher => Teacher,
        Role.Student => Student,
        Role.Parent => Parent,
        _ => throw new ArgumentOutOfRangeException(nameof(role), role, null),
    };

    public static string DisplayName(string role) => role switch
    {
        Teacher => "Учитель",
        ClassTeacher => "Классный руководитель",
        HeadTeacher => "Завуч",
        Admin => "Администратор",
        Parent => "Родитель",
        Student => "Ученик",
        _ => role,
    };

    /// <summary>Стартовая страница роли — куда попадаем после входа или переключения.</summary>
    public static string HomePage(string role) => role switch
    {
        Teacher => "/teacher",
        ClassTeacher => "/class-teacher",
        HeadTeacher => "/head-teacher",
        Admin => "/admin",
        Parent => "/parent",
        Student => "/student",
        _ => "/",
    };
}
