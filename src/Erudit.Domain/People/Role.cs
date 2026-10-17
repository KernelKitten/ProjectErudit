namespace Erudit.Domain.People;

/// <summary>
/// Роли пользователя. Классный руководитель здесь не роль:
/// это вычисляется по ссылке SchoolClass.ClassTeacherId.
/// </summary>
public enum Role
{
    Admin = 1,
    HeadTeacher = 2, // завуч
    Teacher = 3,
    Student = 4,
    Parent = 5,
}

/// <summary>Связь «человек — роль» (у человека может быть несколько ролей).</summary>
public class PersonRole
{
    public int PersonId { get; set; }
    public Person Person { get; set; } = null!;

    public Role Role { get; set; }
}
