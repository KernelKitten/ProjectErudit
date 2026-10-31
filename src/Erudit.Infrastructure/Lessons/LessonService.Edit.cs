using Erudit.Domain;
using Erudit.Domain.Calendar;
using Erudit.Domain.Lessons;
using Microsoft.EntityFrameworkCore;

namespace Erudit.Infrastructure.Lessons;

/// <summary>Что меняем у занятия из серии — как в Google Календаре.</summary>
public enum EditScope
{
    OnlyThis = 1,         // только это занятие
    ThisAndFollowing = 2, // это и все последующие
}

/// <summary>
/// Изменение, отмена и возврат занятий (этап 4, шаг 4, правила согласованы 2026-10-29):
///  - менять и отменять можно только запланированные занятия (проведённые и «не состоялось» — нет),
///    в том числе прошедшие, если месяц не закрыт;
///  - «только это» меняет одно занятие (оно остаётся в серии);
///  - «это и все последующие» делит серию: старая заканчивается накануне, новая — с этого занятия;
///    будущие запланированные занятия переходят в новую серию с новыми параметрами;
///    сменилась дата (день недели) — старые будущие занятия отменяются, по новому дню создаются новые;
///  - ученики: что учитель добавил или убрал в этом занятии, то же — в серии и во всех её будущих занятиях
///    (ученик, отказавшийся от одного занятия, так и остаётся отказавшимся);
///  - конфликты — как при создании; изменяемые занятия сами с собой не конфликтуют.
/// </summary>
public sealed partial class LessonService
{
    /// <summary>Причина у занятий, отменённых из-за переноса серии на другой день.</summary>
    public const string MovedReason = "Занятие перенесено на другой день";

    // ---------------------------------------------------------------- Изменение

    public async Task<LessonCheck> CheckUpdateAsync(int lessonId, LessonDraft d, EditScope scope, LessonActor actor,
        CancellationToken ct = default) =>
        (await PlanUpdateAsync(lessonId, d, scope, actor, ct)).Check;

    /// <summary>
    /// Меняет занятие (или его и все последующие). Ошибки → исключение; предупреждения → исключение без подтверждения.
    /// Возвращает, сколько занятий изменено или создано.
    /// </summary>
    public async Task<int> UpdateAsync(int lessonId, LessonDraft d, EditScope scope, LessonActor actor, bool warningsConfirmed,
        CancellationToken ct = default)
    {
        var plan = await PlanUpdateAsync(lessonId, d, scope, actor, ct);
        ThrowIfCannot(plan.Check, warningsConfirmed);
        var lesson = plan.Lesson!;
        var now = time.GetUtcNow().UtcDateTime;

        if (plan.Scope == EditScope.OnlyThis)
        {
            Apply(lesson, d, d.Date);
            ApplyStudents(lesson, plan.Added, plan.Removed, EnrollmentSource.Teacher, now, reactivateDeclined: true);
            await db.SaveChangesAsync(ct);
            return 1;
        }

        // ---- Это и все последующие.
        var old = plan.Series!;
        var pivot = plan.OriginalDate;
        var students = plan.SeriesStudents.Except(plan.Removed).Union(plan.Added).ToList();
        var hasEarlier = await db.Lessons.AnyAsync(l => l.SeriesId == old.Id && l.StartsAt < SchoolTime.ToUtc(pivot, TimeOnly.MinValue), ct);

        Series target;
        if (hasEarlier)
        {
            // Делим серию: старая заканчивается накануне, новая — с этого занятия (запись на серию — копия).
            target = new Series { TeacherId = old.TeacherId, ToDate = old.ToDate };
            old.ToDate = pivot.AddDays(-1);
            db.Series.Add(target);
            foreach (var id in students)
            {
                db.SeriesEnrollments.Add(new SeriesEnrollment { Series = target, StudentId = id, CreatedAt = now });
            }
        }
        else
        {
            // Меняем первое занятие серии — делить нечего, правим саму серию.
            target = old;
            var enrolled = await db.SeriesEnrollments.Where(e => e.SeriesId == old.Id).ToListAsync(ct);
            db.SeriesEnrollments.RemoveRange(enrolled.Where(e => plan.Removed.Contains(e.StudentId)));
            foreach (var id in plan.Added.Where(id => enrolled.All(e => e.StudentId != id)))
            {
                db.SeriesEnrollments.Add(new SeriesEnrollment { SeriesId = old.Id, StudentId = id, CreatedAt = now });
            }
        }
        target.SubjectId = d.SubjectId;
        target.RoomId = d.RoomId;
        target.DayOfWeek = d.Date.DayOfWeek;
        target.LessonNumber = d.LessonNumber;
        target.StartTime = d.StartTime;
        target.DurationMinutes = d.DurationMinutes;
        target.Capacity = d.Capacity;
        target.Topic = Clean(d.Topic);
        target.Comment = Clean(d.Comment);
        target.FromDate = d.Date;

        if (!plan.Regenerate)
        {
            foreach (var l in plan.Affected)
            {
                l.Series = target;
                Apply(l, d, SchoolDate(l.StartsAt));
                ApplyStudents(l, plan.Added, plan.Removed, EnrollmentSource.Series, now, reactivateDeclined: l.Id == lesson.Id);
            }
            await db.SaveChangesAsync(ct);
            return plan.Affected.Count;
        }

        // Сменилась дата: старые будущие занятия отменяем, по новому дню недели создаём новые.
        foreach (var l in plan.Affected)
        {
            l.Status = LessonStatus.Cancelled;
            l.CancelReason = MovedReason;
        }
        var created = SeriesPlanner.CreateLessons(target, plan.Calendar!);
        foreach (var l in created)
        {
            foreach (var id in students)
            {
                l.Enrollments.Add(new Enrollment { StudentId = id, Source = EnrollmentSource.Series, CreatedAt = now });
            }
            db.Lessons.Add(l);
        }
        await db.SaveChangesAsync(ct); // всё — одной транзакцией
        return created.Count;
    }

    private sealed record UpdatePlan(LessonCheck Check)
    {
        public Lesson? Lesson { get; init; }
        public Series? Series { get; init; }
        public EditScope Scope { get; init; }
        public DateOnly OriginalDate { get; init; }
        /// <summary>Запланированные занятия серии с этого и дальше (для «это и все последующие»).</summary>
        public List<Lesson> Affected { get; init; } = [];
        /// <summary>Дата сменилась — старые занятия отменяем и создаём новые.</summary>
        public bool Regenerate { get; init; }
        public WorkingDayCalendar? Calendar { get; init; }
        public List<int> Added { get; init; } = [];
        public List<int> Removed { get; init; } = [];
        public List<int> SeriesStudents { get; init; } = [];
    }

    private async Task<UpdatePlan> PlanUpdateAsync(int lessonId, LessonDraft d, EditScope scope, LessonActor actor, CancellationToken ct)
    {
        var errors = new List<string>();
        var warnings = new List<string>();
        UpdatePlan Fail() => new(new LessonCheck([], errors, warnings));

        var lesson = await db.Lessons.Include(l => l.Enrollments).Include(l => l.Series)
            .SingleOrDefaultAsync(l => l.Id == lessonId, ct);
        if (lesson is null)
        {
            errors.Add("Занятие не найдено.");
            return Fail();
        }
        CheckCanChange(lesson, actor, errors);
        if (d.TeacherId != lesson.TeacherId)
        {
            errors.Add("Сменить учителя у занятия нельзя.");
        }
        if (d.DurationMinutes <= 0)
        {
            errors.Add("Длительность должна быть больше нуля.");
        }
        if (d.Capacity < 1)
        {
            errors.Add("Мест должно быть хотя бы одно.");
        }
        if (errors.Count > 0)
        {
            return Fail();
        }

        // Ученики — как изменение относительно этого занятия: кого добавили, кого убрали.
        var active = lesson.Enrollments.Where(e => e.Status == EnrollmentStatus.Active).Select(e => e.StudentId).ToHashSet();
        var wanted = d.StudentIds.Distinct().ToList();
        var added = wanted.Where(id => !active.Contains(id)).ToList();
        var removed = active.Where(id => !wanted.Contains(id)).ToList();
        await CheckReferencesAsync(d with { StudentIds = added }, added, errors, ct);
        if (errors.Count > 0)
        {
            return Fail();
        }

        var original = SchoolDate(lesson.StartsAt);
        if (lesson.Series is null)
        {
            scope = EditScope.OnlyThis;
        }

        if (scope == EditScope.OnlyThis)
        {
            if (wanted.Count > d.Capacity)
            {
                errors.Add($"Учеников ({wanted.Count}) больше, чем мест ({d.Capacity}).");
            }
            var calendar = await LoadCalendarAsync(d.Date, d.Date, ct);
            if (d.Date != original && !calendar.IsWorkingDay(d.Date))
            {
                warnings.Add($"{Day(d.Date)} — нерабочий день.");
            }
            await CheckClosedAsync([original, d.Date], errors, ct);
            await CheckConflictsAsync(d, [d.Date], errors, warnings, ct, [lesson.Id]);
            return new UpdatePlan(new LessonCheck([d.Date], errors, warnings))
            {
                Lesson = lesson, Scope = scope, OriginalDate = original, Added = added, Removed = removed,
            };
        }

        // ---- Это и все последующие.
        var series = lesson.Series!;
        var fromUtc = SchoolTime.ToUtc(original, TimeOnly.MinValue);
        var affected = await db.Lessons.Include(l => l.Enrollments)
            .Where(l => l.SeriesId == series.Id && l.Status == LessonStatus.Planned && l.StartsAt >= fromUtc)
            .OrderBy(l => l.StartsAt)
            .ToListAsync(ct);
        var seriesStudents = await db.SeriesEnrollments.Where(e => e.SeriesId == series.Id).Select(e => e.StudentId).ToListAsync(ct);
        var newSeriesStudents = seriesStudents.Except(removed).Union(added).Count();
        var regenerate = d.Date != original;

        List<DateOnly> dates;
        WorkingDayCalendar? cal = null;
        var skipped = new List<DateOnly>();
        if (regenerate)
        {
            if (d.Date > series.ToDate)
            {
                errors.Add($"Новая дата позже конца серии ({series.ToDate:dd.MM.yyyy}). Измените только это занятие.");
                return Fail();
            }
            cal = await LoadCalendarAsync(d.Date, series.ToDate, ct);
            var plannedSeries = new Series { DayOfWeek = d.Date.DayOfWeek, FromDate = d.Date, ToDate = series.ToDate };
            dates = SeriesPlanner.GetLessonDates(plannedSeries, cal).ToList();
            skipped = SkippedDates(d.Date.DayOfWeek, d.Date, series.ToDate, cal);
            if (dates.Count == 0)
            {
                errors.Add("С новой даты до конца серии нет ни одного рабочего дня.");
                return Fail();
            }
            if (newSeriesStudents > d.Capacity)
            {
                errors.Add($"Учеников серии ({newSeriesStudents}) больше, чем мест ({d.Capacity}).");
            }
            await CheckClosedAsync(affected.Select(l => SchoolDate(l.StartsAt)).Concat(dates), errors, ct);
        }
        else
        {
            dates = affected.Select(l => SchoolDate(l.StartsAt)).ToList();
            // Мест должно хватить в каждом занятии — с учётом тех, кого записали только на него.
            int After(Lesson l) => l.Id == lesson.Id
                ? wanted.Count
                : l.Enrollments.Count(e => e.Status == EnrollmentStatus.Active && !removed.Contains(e.StudentId))
                  + added.Count(id => l.Enrollments.All(e => e.StudentId != id || e.Status == EnrollmentStatus.Removed));
            var full = affected
                .Where(l => After(l) > d.Capacity)
                .Select(l => SchoolDate(l.StartsAt).ToString("dd.MM"))
                .ToList();
            if (full.Count > 0)
            {
                errors.Add($"Учеников будет больше, чем мест ({d.Capacity}): {string.Join(", ", full)}.");
            }
            await CheckClosedAsync(dates, errors, ct);
        }
        await CheckConflictsAsync(d, dates, errors, warnings, ct, affected.Select(l => l.Id).ToList());

        return new UpdatePlan(new LessonCheck(dates, errors, warnings) { SkippedDates = skipped })
        {
            Lesson = lesson, Series = series, Scope = scope, OriginalDate = original, Affected = affected,
            Regenerate = regenerate, Calendar = cal, Added = added, Removed = removed, SeriesStudents = seriesStudents,
        };
    }

    // ---------------------------------------------------------------- Отмена

    /// <summary>Какие занятия будут отменены (даты) и можно ли.</summary>
    public async Task<LessonCheck> CheckCancelAsync(int lessonId, EditScope scope, LessonActor actor, CancellationToken ct = default) =>
        (await PlanCancelAsync(lessonId, scope, actor, ct)).Check;

    /// <summary>Отменяет занятие (или его и все последующие: серия заканчивается накануне). Возвращает, сколько отменено.</summary>
    public async Task<int> CancelAsync(int lessonId, EditScope scope, string? reason, LessonActor actor, CancellationToken ct = default)
    {
        var (check, lessons, series, original) = await PlanCancelAsync(lessonId, scope, actor, ct);
        ThrowIfCannot(check, warningsConfirmed: true);
        foreach (var l in lessons)
        {
            l.Status = LessonStatus.Cancelled;
            l.CancelReason = Clean(reason);
        }
        if (series is not null && original.AddDays(-1) >= series.FromDate)
        {
            series.ToDate = original.AddDays(-1);
        }
        await db.SaveChangesAsync(ct);
        return lessons.Count;
    }

    private async Task<(LessonCheck Check, List<Lesson> Lessons, Series? Series, DateOnly Original)> PlanCancelAsync(
        int lessonId, EditScope scope, LessonActor actor, CancellationToken ct)
    {
        var errors = new List<string>();
        var lesson = await db.Lessons.Include(l => l.Series).SingleOrDefaultAsync(l => l.Id == lessonId, ct);
        if (lesson is null)
        {
            errors.Add("Занятие не найдено.");
            return (new LessonCheck([], errors, []), [], null, default);
        }
        CheckCanChange(lesson, actor, errors);
        var original = SchoolDate(lesson.StartsAt);
        var lessons = new List<Lesson> { lesson };
        Series? series = null;
        if (scope == EditScope.ThisAndFollowing && lesson.Series is not null)
        {
            series = lesson.Series;
            var fromUtc = SchoolTime.ToUtc(original, TimeOnly.MinValue);
            lessons = await db.Lessons
                .Where(l => l.SeriesId == series.Id && l.Status == LessonStatus.Planned && l.StartsAt >= fromUtc)
                .OrderBy(l => l.StartsAt)
                .ToListAsync(ct);
        }
        var dates = lessons.Select(l => SchoolDate(l.StartsAt)).ToList();
        await CheckClosedAsync(dates, errors, ct);
        return (new LessonCheck(dates, errors, []), lessons, series, original);
    }

    // ---------------------------------------------------------------- Возврат отменённого

    public async Task<LessonCheck> CheckRestoreAsync(int lessonId, LessonActor actor, CancellationToken ct = default) =>
        (await PlanRestoreAsync(lessonId, actor, ct)).Check;

    /// <summary>Возвращает отменённое занятие в «Запланировано» (если время учителя всё ещё свободно).</summary>
    public async Task RestoreAsync(int lessonId, LessonActor actor, bool warningsConfirmed, CancellationToken ct = default)
    {
        var (check, lesson) = await PlanRestoreAsync(lessonId, actor, ct);
        ThrowIfCannot(check, warningsConfirmed);
        lesson!.Status = LessonStatus.Planned;
        lesson.CancelReason = null;
        await db.SaveChangesAsync(ct);
    }

    private async Task<(LessonCheck Check, Lesson? Lesson)> PlanRestoreAsync(int lessonId, LessonActor actor, CancellationToken ct)
    {
        var errors = new List<string>();
        var warnings = new List<string>();
        var lesson = await db.Lessons.SingleOrDefaultAsync(l => l.Id == lessonId, ct);
        if (lesson is null || lesson.Status != LessonStatus.Cancelled)
        {
            errors.Add("Вернуть можно только отменённое занятие.");
            return (new LessonCheck([], errors, warnings), null);
        }
        if (!actor.IsManager && lesson.TeacherId != actor.PersonId)
        {
            errors.Add("Учитель может менять только свои занятия.");
        }
        var date = SchoolDate(lesson.StartsAt);
        await CheckClosedAsync([date], errors, ct);
        var draft = new LessonDraft
        {
            TeacherId = lesson.TeacherId, SubjectId = lesson.SubjectId, RoomId = lesson.RoomId, Date = date,
            StartTime = TimeOnly.FromDateTime(SchoolTime.ToSchool(lesson.StartsAt)), DurationMinutes = lesson.DurationMinutes,
        };
        await CheckConflictsAsync(draft, [date], errors, warnings, ct, [lesson.Id]);
        return (new LessonCheck([date], errors, warnings), lesson);
    }

    // ---------------------------------------------------------------- Вспомогательное

    private static void CheckCanChange(Lesson lesson, LessonActor actor, List<string> errors)
    {
        if (!actor.IsManager && lesson.TeacherId != actor.PersonId)
        {
            errors.Add("Учитель может менять только свои занятия.");
        }
        if (lesson.Status != LessonStatus.Planned)
        {
            errors.Add(lesson.Status == LessonStatus.Cancelled
                ? "Занятие отменено — сначала верните его."
                : "Занятие уже отмечено (проведено или не состоялось) — чтобы изменить его, сначала снимите отметку.");
        }
    }

    private static void ThrowIfCannot(LessonCheck check, bool warningsConfirmed)
    {
        if (!check.CanCreate)
        {
            throw new InvalidOperationException(string.Join(" ", check.Errors));
        }
        if (check.Warnings.Count > 0 && !warningsConfirmed)
        {
            throw new InvalidOperationException("Нужно подтверждение: " + string.Join(" ", check.Warnings));
        }
    }

    private async Task CheckClosedAsync(IEnumerable<DateOnly> dates, List<string> errors, CancellationToken ct)
    {
        var closed = (await db.ClosedPeriods.Select(p => p.Month).ToListAsync(ct)).ToHashSet();
        var months = dates.Select(x => new DateOnly(x.Year, x.Month, 1)).Distinct().Where(closed.Contains).Order().ToList();
        if (months.Count > 0)
        {
            errors.Add($"Период закрыт ({string.Join(", ", months.Select(m => m.ToString("MMMM yyyy", Ru)))}): " +
                       "занятия в закрытом месяце менять нельзя. Открыть период может завуч.");
        }
    }

    /// <summary>Поля занятия из черновика; дата — своя (у серии каждое занятие в свой день).</summary>
    private static void Apply(Lesson l, LessonDraft d, DateOnly date)
    {
        l.SubjectId = d.SubjectId;
        l.RoomId = d.RoomId;
        l.StartsAt = SchoolTime.ToUtc(date, d.StartTime);
        l.DurationMinutes = d.DurationMinutes;
        l.LessonNumber = d.LessonNumber;
        l.Capacity = d.Capacity;
        l.Topic = Clean(d.Topic);
        l.Comment = Clean(d.Comment);
    }

    /// <param name="reactivateDeclined">
    /// Для самого изменяемого занятия: учитель явно вернул отказавшегося ученика. В остальных занятиях серии
    /// отказ от конкретного занятия сохраняем.
    /// </param>
    private static void ApplyStudents(Lesson l, List<int> added, List<int> removed, EnrollmentSource source, DateTime now,
        bool reactivateDeclined)
    {
        foreach (var id in added)
        {
            var e = l.Enrollments.FirstOrDefault(x => x.StudentId == id);
            if (e is null)
            {
                l.Enrollments.Add(new Enrollment { StudentId = id, Source = source, CreatedAt = now });
            }
            else if (e.Status == EnrollmentStatus.Removed || (reactivateDeclined && e.Status == EnrollmentStatus.Declined))
            {
                e.Status = EnrollmentStatus.Active;
            }
        }
        foreach (var e in l.Enrollments.Where(x => x.Status == EnrollmentStatus.Active && removed.Contains(x.StudentId)))
        {
            e.Status = EnrollmentStatus.Removed;
        }
    }

    private static DateOnly SchoolDate(DateTime utc) => DateOnly.FromDateTime(SchoolTime.ToSchool(utc));
}
