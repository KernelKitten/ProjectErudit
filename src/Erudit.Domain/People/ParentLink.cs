namespace Erudit.Domain.People;

/// <summary>Связь «родитель — ребёнок». Создаётся по телефону из списка класса или по коду классного руководителя.</summary>
public class ParentLink
{
    public int ParentId { get; set; }
    public Person Parent { get; set; } = null!;

    public int StudentId { get; set; }
    public Person Student { get; set; } = null!;

    /// <summary>
    /// Получать все уведомления о ребёнке, а не только сводку
    /// (для начальной школы, где у ребёнка обычно нет MAX).
    /// </summary>
    public bool NotifyAll { get; set; }

    public DateTime CreatedAt { get; set; }
}
