using System.Globalization;
using Erudit.Domain;
using Erudit.Domain.Calendar;
using Erudit.Domain.Lessons;
using Erudit.Domain.People;
using Erudit.Infrastructure.Persistence;
using Microsoft.EntityFrameworkCore;

namespace Erudit.Infrastructure.Lessons;

/// <summary>Кто действует: учитель правит только свои занятия, администратор и завуч — любые.</summary>
public sealed record LessonActor(int PersonId, bool IsManager);

/// <summary>
/// Что хочет создать учитель: разовое занятие (RepeatDays пустой) или регулярное —
/// каждую неделю по выбранным дням с Date по RepeatUntil (на каждый день недели — своя серия).
/// </summary>
public sealed record LessonDraft
{
    public required int TeacherId { get; init; }
    public required int SubjectId { get; init; }
    public required int RoomId { get; init; }

    /// <summary>Дата разового занятия или первый день повторения.</summary>
    public required DateOnly Date { get; init; }

    public IReadOnlyList<DayOfWeek> RepeatDays { get; init; } = [];
    public DateOnly? RepeatUntil { get; init; }

    /// <summary>Номер урока, если время взято из звонков; пусто — время вручную.</summary>
    public int? LessonNumber { get; init; }
    public required TimeOnly StartTime { get; init; }
    public required int DurationMinutes { get; init; }

    public int Capacity { get; init; } = 1;
    public string? Topic { get; init; }
    public string? Comment { get; init; }
    public IReadOnlyList<int> StudentIds { get; init; } = [];

    public bool IsRepeating => RepeatDays.Count > 0;
}

/// <summary>
/// Результат проверки: даты будущих занятий, ошибки (создать нельзя) и предупреждения
/// (создать можно, но только после подтверждения учителя).
/// </summary>
public sealed record LessonCheck(IReadOnlyList<DateOnly> Dates, IReadOnlyList<string> Errors, IReadOnlyList<string> Warnings)
{
    public bool CanCreate => Errors.Count == 0;

    /// <summary>У повторения: дни, которые пропущены, потому что нерабочие (чтобы учитель заметил праздники).</summary>
    public IReadOnlyList<DateOnly> SkippedDates { get; init; } = [];
}

/// <summary>Быстрый выбор окончания повторения: «до конца четверти» и т. п.</summary>
public sealed record RepeatEnd(string Label, DateOnly Date);

/// <summary>
/// Создание занятий (этап 4, правила согласованы 2026-10-19):
///  - у учителя в это время уже есть занятие (не отменённое) → нельзя;
///  - кабинет занят другим учителем → предупреждение;
///  - разовое на нерабочий день → предупреждение; серия нерабочие дни пропускает сама;
///  - в закрытом месяце создавать нельзя; задним числом — можно;
///  - учеников не больше, чем мест.
/// </summary>
public sealed class LessonService(EruditDbContext db, TimeProvider time)
{
    private static readonly CultureInfo Ru = CultureInfo.GetCultureInfo("ru-RU");

    /// <summary>Сколько конфликтов перечислять в сообщении; остальные — «и ещё N».</summary>
    private const int MaxListed = 5;

    public async Task<LessonCheck> CheckAsync(LessonDraft d, LessonActor actor, CancellationToken ct = default)
    {
        var errors = new List<string>();
        var warnings = new List<string>();

        if (!actor.IsManager && d.TeacherId != actor.PersonId)
        {
            errors.Add("Учитель может создавать только свои занятия.");
        }
        if (d.DurationMinutes <= 0)
        {
            errors.Add("Длительность должна быть больше нуля.");
        }
        if (d.Capacity < 1)
        {
            errors.Add("Мест должно быть хотя бы одно.");
        }
        var studentIds = d.StudentIds.Distinct().ToList();
        if (studentIds.Count > d.Capacity)
        {
            errors.Add($"Учеников ({studentIds.Count}) больше, чем мест ({d.Capacity}).");
        }
        if (d.IsRepeating && d.RepeatUntil is null)
        {
            errors.Add("Укажите, до какой даты повторять.");
        }
        else if (d.IsRepeating && d.RepeatUntil < d.Date)
        {
            errors.Add("Дата окончания повторения раньше даты начала.");
        }
        if (errors.Count > 0)
        {
            return new LessonCheck([], errors, warnings);
        }

        await CheckReferencesAsync(d, studentIds, errors, ct);
        if (errors.Count > 0)
        {
            return new LessonCheck([], errors, warnings);
        }

        var calendar = await LoadCalendarAsync(d.Date, d.RepeatUntil ?? d.Date, ct);
        var dates = PlanDates(d, calendar);
        if (dates.Count == 0)
        {
            errors.Add($"В выбранные дни недели с {d.Date:dd.MM.yyyy} по {d.RepeatUntil:dd.MM.yyyy} нет ни одного рабочего дня.");
            return new LessonCheck(dates, errors, warnings);
        }
        if (!d.IsRepeating && !calendar.IsWorkingDay(d.Date))
        {
            warnings.Add($"{Day(d.Date)} — нерабочий день.");
        }

        var closed = (await db.ClosedPeriods.Select(p => p.Month).ToListAsync(ct)).ToHashSet();
        var closedMonths = dates.Select(x => new DateOnly(x.Year, x.Month, 1)).Distinct().Where(closed.Contains).ToList();
        if (closedMonths.Count > 0)
        {
            errors.Add($"Период закрыт ({string.Join(", ", closedMonths.Select(m => m.ToString("MMMM yyyy", Ru)))}): " +
                       "занятия в закрытом месяце создавать нельзя. Открыть период может завуч.");
        }

        await CheckConflictsAsync(d, dates, errors, warnings, ct);
        return new LessonCheck(dates, errors, warnings) { SkippedDates = SkippedDates(d, calendar) };
    }

    /// <summary>
    /// Создаёт занятия. Ошибки → исключение; предупреждения → исключение, если учитель их не подтвердил.
    /// Возвращает, сколько занятий создано.
    /// </summary>
    public async Task<int> CreateAsync(LessonDraft d, LessonActor actor, bool warningsConfirmed, CancellationToken ct = default)
    {
        var check = await CheckAsync(d, actor, ct);
        if (!check.CanCreate)
        {
            throw new InvalidOperationException(string.Join(" ", check.Errors));
        }
        if (check.Warnings.Count > 0 && !warningsConfirmed)
        {
            throw new InvalidOperationException("Нужно подтверждение: " + string.Join(" ", check.Warnings));
        }

        var now = time.GetUtcNow().UtcDateTime;
        var studentIds = d.StudentIds.Distinct().ToList();

        if (!d.IsRepeating)
        {
            var lesson = NewLesson(d, d.Date);
            foreach (var id in studentIds)
            {
                lesson.Enrollments.Add(new Enrollment { StudentId = id, Source = EnrollmentSource.Teacher, CreatedAt = now });
            }
            db.Lessons.Add(lesson);
            await db.SaveChangesAsync(ct);
            return 1;
        }

        var calendar = await LoadCalendarAsync(d.Date, d.RepeatUntil!.Value, ct);
        var created = 0;
        foreach (var day in d.RepeatDays.Distinct().OrderBy(x => ((int)x + 6) % 7)) // с понедельника
        {
            var series = new Series
            {
                TeacherId = d.TeacherId,
                SubjectId = d.SubjectId,
                RoomId = d.RoomId,
                DayOfWeek = day,
                LessonNumber = d.LessonNumber,
                StartTime = d.StartTime,
                DurationMinutes = d.DurationMinutes,
                Capacity = d.Capacity,
                Topic = Clean(d.Topic),
                Comment = Clean(d.Comment),
                FromDate = d.Date,
                ToDate = d.RepeatUntil.Value,
            };
            var lessons = SeriesPlanner.CreateLessons(series, calendar);
            if (lessons.Count == 0)
            {
                continue; // в этот день недели рабочих дней нет — серию не создаём
            }
            db.Series.Add(series);
            foreach (var id in studentIds)
            {
                db.SeriesEnrollments.Add(new SeriesEnrollment { Series = series, StudentId = id, CreatedAt = now });
            }
            foreach (var lesson in lessons)
            {
                foreach (var id in studentIds)
                {
                    lesson.Enrollments.Add(new Enrollment { StudentId = id, Source = EnrollmentSource.Series, CreatedAt = now });
                }
                db.Lessons.Add(lesson);
            }
            created += lessons.Count;
        }

        await db.SaveChangesAsync(ct); // все серии и занятия — одной транзакцией
        return created;
    }

    /// <summary>
    /// Варианты «повторять до…» для даты: конец четверти (текущей, или следующей, если сейчас каникулы),
    /// конец полугодия (2-й или 4-й четверти) и конец учебного года (4-й четверти или самого года).
    /// </summary>
    public async Task<IReadOnlyList<RepeatEnd>> GetRepeatEndsAsync(DateOnly date, CancellationToken ct = default)
    {
        var year = await db.AcademicYears.FirstOrDefaultAsync(y => y.StartDate <= date && y.EndDate >= date, ct);
        if (year is null)
        {
            return [];
        }
        var quarters = await db.Quarters.Where(q => q.AcademicYearId == year.Id).OrderBy(q => q.Number).ToListAsync(ct);
        var result = new List<RepeatEnd>();
        var quarter = quarters.FirstOrDefault(q => q.EndDate >= date);
        if (quarter is not null)
        {
            result.Add(new RepeatEnd($"До конца {quarter.Number}-й четверти", quarter.EndDate));
            var halfEnd = quarters.FirstOrDefault(q => q.Number == (quarter.Number <= 2 ? 2 : 4));
            if (halfEnd is not null && halfEnd.EndDate > quarter.EndDate)
            {
                result.Add(new RepeatEnd("До конца полугодия", halfEnd.EndDate));
            }
        }
        var yearEnd = quarters.FirstOrDefault(q => q.Number == 4)?.EndDate ?? year.EndDate;
        if (yearEnd >= date && result.All(r => r.Date != yearEnd))
        {
            result.Add(new RepeatEnd("До конца учебного года", yearEnd));
        }
        return result;
    }

    // ---------------------------------------------------------------- Вспомогательное

    /// <summary>Даты будущих занятий: разовое — сама дата; регулярное — по всем выбранным дням недели.</summary>
    private static List<DateOnly> PlanDates(LessonDraft d, WorkingDayCalendar calendar)
    {
        if (!d.IsRepeating)
        {
            return [d.Date];
        }
        return d.RepeatDays.Distinct()
            .SelectMany(day => SeriesPlanner.GetLessonDates(
                new Series { DayOfWeek = day, FromDate = d.Date, ToDate = d.RepeatUntil!.Value }, calendar))
            .Order()
            .ToList();
    }

    /// <summary>Дни повторения (по выбранным дням недели), которые серия пропустит как нерабочие.</summary>
    private static List<DateOnly> SkippedDates(LessonDraft d, WorkingDayCalendar calendar)
    {
        if (!d.IsRepeating)
        {
            return [];
        }
        var days = d.RepeatDays.ToHashSet();
        var result = new List<DateOnly>();
        for (var date = d.Date; date <= d.RepeatUntil!.Value; date = date.AddDays(1))
        {
            if (days.Contains(date.DayOfWeek) && !calendar.IsWorkingDay(date))
            {
                result.Add(date);
            }
        }
        return result;
    }

    private async Task CheckReferencesAsync(LessonDraft d, List<int> studentIds, List<string> errors, CancellationToken ct)
    {
        var teacherOk = await db.People.AnyAsync(p => p.Id == d.TeacherId && p.IsActive
            && p.Roles.Any(r => r.Role == Role.Teacher), ct);
        if (!teacherOk)
        {
            errors.Add("Учитель не найден или уволился.");
        }
        if (!await db.Subjects.AnyAsync(s => s.Id == d.SubjectId && s.IsActive, ct))
        {
            errors.Add("Предмет не найден или выключен.");
        }
        if (!await db.Rooms.AnyAsync(r => r.Id == d.RoomId && r.IsActive, ct))
        {
            errors.Add("Кабинет не найден или выключен.");
        }
        if (studentIds.Count > 0)
        {
            var found = await db.People.CountAsync(p => studentIds.Contains(p.Id) && p.IsActive
                && p.Roles.Any(r => r.Role == Role.Student), ct);
            if (found != studentIds.Count)
            {
                errors.Add("Некоторые ученики не найдены или выбыли — обновите список.");
            }
        }
    }

    private async Task CheckConflictsAsync(LessonDraft d, List<DateOnly> dates, List<string> errors, List<string> warnings,
        CancellationToken ct)
    {
        var planned = dates.Select(x =>
        {
            var start = SchoolTime.ToUtc(x, d.StartTime);
            return (Start: start, End: start.AddMinutes(d.DurationMinutes));
        }).ToList();
        // Занятие, начавшееся до нашего, тоже может с ним пересечься — берём с запасом в полдня.
        var from = planned.Min(p => p.Start).AddHours(-12);
        var to = planned.Max(p => p.End);

        var existing = await db.Lessons
            .Where(l => l.Status != LessonStatus.Cancelled && l.StartsAt >= from && l.StartsAt < to
                        && (l.TeacherId == d.TeacherId || l.RoomId == d.RoomId))
            .Select(l => new
            {
                l.StartsAt,
                l.DurationMinutes,
                l.TeacherId,
                Subject = l.Subject.Name,
                Room = l.Room.Name,
                Teacher = l.Teacher.LastName + " " + l.Teacher.FirstName.Substring(0, 1) + ".",
            })
            .ToListAsync(ct);

        var teacherBusy = new List<string>();
        var roomBusy = new List<string>();
        foreach (var e in existing.OrderBy(e => e.StartsAt))
        {
            var end = e.StartsAt.AddMinutes(e.DurationMinutes);
            if (!planned.Any(p => e.StartsAt < p.End && p.Start < end))
            {
                continue;
            }
            var when = $"{Day(DateOnly.FromDateTime(SchoolTime.ToSchool(e.StartsAt)))} {SchoolTime.ToSchool(e.StartsAt):HH:mm}";
            if (e.TeacherId == d.TeacherId)
            {
                teacherBusy.Add($"{when} — {e.Subject}, {e.Room}");
            }
            else
            {
                roomBusy.Add($"{when} — {e.Teacher}, {e.Subject}");
            }
        }
        if (teacherBusy.Count > 0)
        {
            errors.Add("В это время у учителя уже есть занятие: " + List(teacherBusy) + ".");
        }
        if (roomBusy.Count > 0)
        {
            warnings.Add("Кабинет в это время занят: " + List(roomBusy) + ".");
        }
    }

    private async Task<WorkingDayCalendar> LoadCalendarAsync(DateOnly from, DateOnly to, CancellationToken ct) =>
        new(await db.CalendarDays.Where(c => c.Date >= from && c.Date <= to).ToListAsync(ct));

    private static Lesson NewLesson(LessonDraft d, DateOnly date) => new()
    {
        TeacherId = d.TeacherId,
        SubjectId = d.SubjectId,
        RoomId = d.RoomId,
        StartsAt = SchoolTime.ToUtc(date, d.StartTime),
        DurationMinutes = d.DurationMinutes,
        LessonNumber = d.LessonNumber,
        Capacity = d.Capacity,
        Topic = Clean(d.Topic),
        Comment = Clean(d.Comment),
    };

    private static string? Clean(string? text) => string.IsNullOrWhiteSpace(text) ? null : text.Trim();

    private static string Day(DateOnly date) => date.ToString("dd.MM.yyyy (ddd)", Ru);

    private static string List(List<string> items) =>
        string.Join("; ", items.Take(MaxListed)) + (items.Count > MaxListed ? $" и ещё {items.Count - MaxListed}" : "");
}
