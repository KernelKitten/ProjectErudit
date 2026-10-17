using Erudit.Domain.People;

namespace Erudit.Domain.Accounting;

/// <summary>
/// Закрытый месяц: занятия за него больше нельзя менять, пока завуч не откроет период.
/// Пока запись есть — месяц закрыт; открыть = удалить запись (факт закрытия остаётся в журнале).
/// </summary>
public class ClosedPeriod
{
    public int Id { get; set; }

    /// <summary>Первое число закрытого месяца.</summary>
    public DateOnly Month { get; set; }

    public int ClosedById { get; set; }
    public Person ClosedBy { get; set; } = null!;

    public DateTime ClosedAt { get; set; }
}

/// <summary>Запись журнала изменений: кто, когда, что и как изменил.</summary>
public class AuditEntry
{
    public long Id { get; set; }
    public DateTime At { get; set; }

    /// <summary>Кто изменил. Пусто — сама система (например, загрузка календаря).</summary>
    public int? PersonId { get; set; }

    public string EntityType { get; set; } = "";
    public string EntityId { get; set; } = "";

    /// <summary>Created / Updated / Deleted.</summary>
    public string Action { get; set; } = "";

    /// <summary>Изменённые поля в формате JSON: {"DurationMinutes": [40, 60]}.</summary>
    public string? Changes { get; set; }
}
