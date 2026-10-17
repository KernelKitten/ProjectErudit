using Erudit.Domain.People;

namespace Erudit.Domain.School;

/// <summary>
/// Класс в конкретном учебном году («8б» в 2026/27). На следующий год создаётся новый класс «9б»,
/// поэтому прошлогодние отчёты не меняются.
/// </summary>
public class SchoolClass
{
    public int Id { get; set; }

    public int AcademicYearId { get; set; }
    public AcademicYear AcademicYear { get; set; } = null!;

    /// <summary>Параллель: 1–11; 0 — подготовительная группа перед первым классом.</summary>
    public int Grade { get; set; }

    /// <summary>Буква: «а», «б»... Для 10 и 11 классов в гимназии может быть пустой.</summary>
    public string Letter { get; set; } = "";

    public int? ClassTeacherId { get; set; }
    public Person? ClassTeacher { get; set; }

    public string Name => $"{Grade}{Letter}";
}

/// <summary>
/// Ученик в классе с даты по дату. При переводе из 7а в 7б старая запись закрывается (ToDate),
/// открывается новая. В каждый момент у ученика ровно один класс.
/// </summary>
public class ClassMembership
{
    public int Id { get; set; }

    public int StudentId { get; set; }
    public Person Student { get; set; } = null!;

    public int SchoolClassId { get; set; }
    public SchoolClass SchoolClass { get; set; } = null!;

    public DateOnly FromDate { get; set; }

    /// <summary>Пусто — ученик всё ещё в этом классе.</summary>
    public DateOnly? ToDate { get; set; }

    public bool IsActiveOn(DateOnly date) => date >= FromDate && (ToDate is null || date <= ToDate);
}
