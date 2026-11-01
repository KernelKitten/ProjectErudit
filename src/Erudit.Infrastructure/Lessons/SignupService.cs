using Erudit.Domain;
using Erudit.Domain.Lessons;
using Erudit.Domain.People;
using Erudit.Infrastructure.Persistence;
using Microsoft.EntityFrameworkCore;

namespace Erudit.Infrastructure.Lessons;

/// <summary>Занятие на странице ученика или родителя. Дата и время — по школьным часам.</summary>
/// <param name="Blocked">Почему записаться нельзя (нет мест, пересечение); пусто — можно.</param>
/// <param name="WasDeclined">Ученик раньше отказался от этого занятия.</param>
/// <param name="HasFollowing">В серии есть ещё занятия, на которые ученик записан или может записаться.</param>
public sealed record SignupLesson(
    int Id,
    DateOnly Date,
    TimeOnly Start,
    TimeOnly End,
    int? LessonNumber,
    string Subject,
    string Teacher,
    string Room,
    string? Topic,
    string? Comment,
    int Capacity,
    int Enrolled,
    bool IsSeries,
    LessonStatus Status,
    string? CancelReason,
    string? Blocked,
    bool WasDeclined,
    bool CanDecline,
    bool HasFollowing);

/// <summary>Страница ученика: на что он записан и куда может записаться.</summary>
public sealed record StudentLessons(IReadOnlyList<SignupLesson> Mine, IReadOnlyList<SignupLesson> Open);

/// <param name="Count">На сколько занятий записан.</param>
/// <param name="Skipped">Занятия серии, на которые записать не получилось: «05.11: Нет свободных мест.»</param>
public sealed record SignupResult(int Count, IReadOnlyList<string> Skipped);

/// <summary>
/// Запись ученика на занятия и отказ (этап 5, правила согласованы 2026-10-31):
///  - действует сам ученик или его родитель (<see cref="ParentLink"/>);
///  - запись и отказ — до начала занятия; лимита нет, подтверждение учителя не нужно;
///  - мест не больше, чем задал учитель, очереди нет; пересечение с другим занятием ученика — нельзя;
///  - записаться можно, если занятие открыто для параллели ученика; отказавшийся может вернуться и в закрытое;
///  - кого убрал учитель, того записывает обратно только учитель;
///  - у регулярного: «только это» или «это и все следующие»; занятия серии, на которые нельзя, пропускаем и называем.
/// Блокировка строк занятий (см. <see cref="SignUpAsync"/>) не даёт двоим занять последнее место.
/// </summary>
public sealed class SignupService(EruditDbContext db, TimeProvider time)
{
    public const string NoSeats = "Нет свободных мест.";

    /// <summary>Что показать на странице: записи с fromUtc и открытые занятия от «сейчас» до toUtc.</summary>
    public async Task<StudentLessons> LoadAsync(int studentId, int actorId, DateTime fromUtc, DateTime toUtc, CancellationToken ct = default)
    {
        await EnsureAllowedAsync(studentId, actorId, ct);
        var now = time.GetUtcNow().UtcDateTime;

        var mine = await QueryLessons()
            .Where(l => l.Enrollments.Any(e => e.StudentId == studentId && e.Status == EnrollmentStatus.Active)
                        && l.StartsAt >= fromUtc && l.StartsAt < toUtc)
            .OrderBy(l => l.StartsAt)
            .ToListAsync(ct);
        var candidates = await QueryLessons()
            .Where(l => l.Status == LessonStatus.Planned && l.StartsAt > now && l.StartsAt < toUtc
                        && !l.Enrollments.Any(e => e.StudentId == studentId && e.Status != EnrollmentStatus.Declined))
            .OrderBy(l => l.StartsAt)
            .ToListAsync(ct);
        var followingSeries = await db.Enrollments
            .Where(e => e.StudentId == studentId && e.Status == EnrollmentStatus.Active
                        && e.Lesson.SeriesId != null && e.Lesson.Status == LessonStatus.Planned && e.Lesson.StartsAt > now)
            .Select(e => new { e.Lesson.SeriesId, e.Lesson.StartsAt })
            .ToListAsync(ct);
        var openSeries = candidates.Where(l => l.SeriesId is not null).Select(l => l.SeriesId).Distinct().ToList();
        var plannedInSeries = (await db.Lessons
                .Where(l => openSeries.Contains(l.SeriesId) && l.Status == LessonStatus.Planned && l.StartsAt > now)
                .Select(l => new { l.SeriesId, l.StartsAt })
                .ToListAsync(ct))
            .ToLookup(l => l.SeriesId);

        var context = await LoadContextAsync(studentId, ct);

        SignupLesson Row(Lesson l, string? blocked, bool hasFollowing)
        {
            var start = SchoolTime.ToSchool(l.StartsAt);
            return new SignupLesson(l.Id, DateOnly.FromDateTime(start), TimeOnly.FromDateTime(start),
                TimeOnly.FromDateTime(start).AddMinutes(l.DurationMinutes), l.LessonNumber, l.Subject.Name, l.Teacher.ShortName, l.Room.Name,
                l.Topic, l.Comment, l.Capacity, l.Enrollments.Count(e => e.Status == EnrollmentStatus.Active), l.SeriesId is not null,
                l.Status, l.CancelReason, blocked,
                l.Enrollments.Any(e => e.StudentId == studentId && e.Status == EnrollmentStatus.Declined),
                l.Status == LessonStatus.Planned && l.StartsAt > now && !context.IsClosed(l), hasFollowing);
        }

        var mineRows = mine.Select(l => Row(l, null,
            followingSeries.Any(f => f.SeriesId == l.SeriesId && f.StartsAt > l.StartsAt))).ToList();
        // Закрытое занятие ученик не видит, пока сам от него не отказался; остальные причины показываем на карточке.
        var openRows = candidates
            .Where(l => l.IsOpenForGrade(context.GradeOn(l)) || l.Enrollments.Any(e => e.StudentId == studentId))
            .Select(l => Row(l, context.WhyNot(l, studentId, now),
                l.SeriesId is not null && plannedInSeries[l.SeriesId].Any(x => x.StartsAt > l.StartsAt)))
            .ToList();
        return new StudentLessons(mineRows, openRows);
    }

    /// <summary>
    /// Записывает ученика. Само занятие должно быть доступно (иначе исключение с причиной),
    /// занятия серии, на которые нельзя, пропускаются. Всё — в одной транзакции под блокировкой:
    /// второй ученик дождётся конца первой записи и увидит уже занятое место.
    /// </summary>
    public async Task<SignupResult> SignUpAsync(int lessonId, int studentId, EditScope scope, int actorId, CancellationToken ct = default)
    {
        await using var transaction = await db.Database.BeginTransactionAsync(ct);
        var source = await EnsureAllowedAsync(studentId, actorId, ct);

        // FOR NO KEY UPDATE — как FOR UPDATE, но не мешает учителю создавать записи и занятия со ссылками на эти строки.
        await db.Database.ExecuteSqlInterpolatedAsync($"SELECT 1 FROM people WHERE id = {studentId} FOR NO KEY UPDATE", ct);

        var target = await db.Lessons.AsNoTracking().Where(l => l.Id == lessonId)
            .Select(l => new { l.SeriesId, l.StartsAt }).SingleOrDefaultAsync(ct)
            ?? throw new InvalidOperationException("Занятие не найдено.");
        var bySeries = scope == EditScope.ThisAndFollowing && target.SeriesId is not null;

        int[] ids = bySeries
            ? await db.Lessons
                .Where(l => l.SeriesId == target.SeriesId && l.Status == LessonStatus.Planned && l.StartsAt >= target.StartsAt)
                .Select(l => l.Id).ToArrayAsync(ct)
            : [lessonId];
        if (!ids.Contains(lessonId))
        {
            ids = [.. ids, lessonId]; // занятие уже не запланировано — причину назовёт WhyNot
        }
        // Всегда в порядке id: две записи на пересекающиеся занятия не заблокируют друг друга намертво.
        await db.Database.ExecuteSqlInterpolatedAsync($"SELECT 1 FROM lessons WHERE id = ANY({ids}) ORDER BY id FOR NO KEY UPDATE", ct);

        var lessons = await db.Lessons.Include(l => l.Enrollments)
            .Where(l => ids.Contains(l.Id))
            .OrderBy(l => l.StartsAt)
            .ToListAsync(ct);
        var context = await LoadContextAsync(studentId, ct);
        var now = time.GetUtcNow().UtcDateTime;

        var count = 0;
        var skipped = new List<string>();
        foreach (var lesson in lessons)
        {
            var enrollment = lesson.Enrollments.FirstOrDefault(e => e.StudentId == studentId);
            if (bySeries && enrollment?.Status == EnrollmentStatus.Active)
            {
                count++; // уже записан на это занятие — считаем, менять нечего
                continue;
            }
            var reason = context.WhyNot(lesson, studentId, now);
            if (reason is not null)
            {
                if (lesson.Id == lessonId)
                {
                    throw new InvalidOperationException(reason);
                }
                skipped.Add($"{SchoolDate(lesson):dd.MM}: {reason}");
                continue;
            }

            var entrySource = bySeries ? EnrollmentSource.Series : source;
            if (enrollment is null)
            {
                lesson.Enrollments.Add(new Enrollment { StudentId = studentId, Source = entrySource, CreatedAt = now });
            }
            else
            {
                enrollment.Status = EnrollmentStatus.Active;
                enrollment.Source = entrySource;
            }
            count++;
        }

        if (bySeries && !await db.SeriesEnrollments.AnyAsync(e => e.SeriesId == target.SeriesId && e.StudentId == studentId, ct))
        {
            db.SeriesEnrollments.Add(new SeriesEnrollment { SeriesId = target.SeriesId!.Value, StudentId = studentId, CreatedAt = now });
        }

        await db.SaveChangesAsync(ct);
        await transaction.CommitAsync(ct);
        return new SignupResult(count, skipped);
    }

    /// <summary>Отказ от занятия (или от него и всех следующих в серии). Возвращает, от скольких занятий отказались.</summary>
    public async Task<int> DeclineAsync(int lessonId, int studentId, EditScope scope, int actorId, CancellationToken ct = default)
    {
        await EnsureAllowedAsync(studentId, actorId, ct);
        var now = time.GetUtcNow().UtcDateTime;

        var lesson = await db.Lessons.Include(l => l.Enrollments).SingleOrDefaultAsync(l => l.Id == lessonId, ct)
            ?? throw new InvalidOperationException("Занятие не найдено.");
        var enrollment = lesson.Enrollments.FirstOrDefault(e => e.StudentId == studentId && e.Status == EnrollmentStatus.Active)
            ?? throw new InvalidOperationException("Ученик не записан на это занятие.");
        if (lesson.Status != LessonStatus.Planned)
        {
            throw new InvalidOperationException("Занятие уже отменено или отмечено.");
        }
        if (lesson.StartsAt <= now)
        {
            throw new InvalidOperationException("Занятие уже началось — отказаться нельзя.");
        }
        var context = await LoadContextAsync(studentId, ct);
        if (context.IsClosed(lesson))
        {
            throw new InvalidOperationException("Период закрыт — запись менять нельзя.");
        }

        enrollment.Status = EnrollmentStatus.Declined;
        var count = 1;
        if (scope == EditScope.ThisAndFollowing && lesson.SeriesId is { } seriesId)
        {
            var following = await db.Enrollments.Include(e => e.Lesson)
                .Where(e => e.StudentId == studentId && e.Status == EnrollmentStatus.Active && e.LessonId != lessonId
                            && e.Lesson.SeriesId == seriesId && e.Lesson.Status == LessonStatus.Planned && e.Lesson.StartsAt > lesson.StartsAt)
                .ToListAsync(ct);
            foreach (var e in following.Where(e => !context.IsClosed(e.Lesson)))
            {
                e.Status = EnrollmentStatus.Declined;
                count++;
            }
            db.SeriesEnrollments.RemoveRange(await db.SeriesEnrollments
                .Where(e => e.SeriesId == seriesId && e.StudentId == studentId).ToListAsync(ct));
        }
        await db.SaveChangesAsync(ct);
        return count;
    }

    // ---------------------------------------------------------------- Вспомогательное

    private IQueryable<Lesson> QueryLessons() => db.Lessons.AsNoTracking()
        .Include(l => l.Subject).Include(l => l.Room).Include(l => l.Teacher).Include(l => l.Enrollments);

    /// <summary>Сам ученик или его родитель; возвращает, кем считать автора записи. Иначе — исключение.</summary>
    private async Task<EnrollmentSource> EnsureAllowedAsync(int studentId, int actorId, CancellationToken ct)
    {
        var isStudent = await db.People.AnyAsync(p => p.Id == studentId && p.IsActive && p.Roles.Any(r => r.Role == Role.Student), ct);
        if (!isStudent)
        {
            throw new InvalidOperationException("Ученик не найден или выбыл.");
        }
        if (actorId == studentId)
        {
            return EnrollmentSource.Self;
        }
        if (await db.ParentLinks.AnyAsync(l => l.ParentId == actorId && l.StudentId == studentId, ct))
        {
            return EnrollmentSource.Parent;
        }
        throw new InvalidOperationException("Записывать ученика может он сам или его родитель.");
    }

    private async Task<StudentContext> LoadContextAsync(int studentId, CancellationToken ct)
    {
        var memberships = await db.ClassMemberships.Where(m => m.StudentId == studentId)
            .Select(m => new Membership(m.FromDate, m.ToDate, m.SchoolClass.Grade))
            .ToListAsync(ct);
        var busy = await db.Enrollments
            .Where(e => e.StudentId == studentId && e.Status == EnrollmentStatus.Active && e.Lesson.Status != LessonStatus.Cancelled)
            .Select(e => new BusyLesson(e.LessonId, e.Lesson.StartsAt, e.Lesson.DurationMinutes, e.Lesson.Subject.Name))
            .ToListAsync(ct);
        var closed = await db.ClosedPeriods.Select(p => p.Month).ToListAsync(ct);
        return new StudentContext(memberships, busy, closed.ToHashSet());
    }

    private static DateOnly SchoolDate(Lesson l) => DateOnly.FromDateTime(SchoolTime.ToSchool(l.StartsAt));

    private sealed record Membership(DateOnly From, DateOnly? To, int Grade);
    private sealed record BusyLesson(int LessonId, DateTime StartsAt, int DurationMinutes, string Subject);

    /// <summary>Всё об ученике, что нужно для решения «можно ли записаться»; одно правило — и для показа, и для записи.</summary>
    private sealed record StudentContext(List<Membership> Memberships, List<BusyLesson> Busy, HashSet<DateOnly> Closed)
    {
        public int? GradeOn(Lesson l)
        {
            var date = SchoolDate(l);
            return Memberships.FirstOrDefault(m => m.From <= date && (m.To is null || m.To >= date))?.Grade;
        }

        public bool IsClosed(Lesson l)
        {
            var date = SchoolDate(l);
            return Closed.Contains(new DateOnly(date.Year, date.Month, 1));
        }

        /// <summary>Почему записаться нельзя; null — можно.</summary>
        public string? WhyNot(Lesson l, int studentId, DateTime now)
        {
            var own = l.Enrollments.FirstOrDefault(e => e.StudentId == studentId);
            if (own?.Status == EnrollmentStatus.Active)
            {
                return "Ученик уже записан.";
            }
            if (own?.Status == EnrollmentStatus.Removed)
            {
                return "Учитель убрал ученика с этого занятия — вернуть может только учитель.";
            }
            if (l.Status != LessonStatus.Planned)
            {
                return "Занятие отменено или уже отмечено.";
            }
            if (l.StartsAt <= now)
            {
                return "Занятие уже началось.";
            }
            if (IsClosed(l))
            {
                return "Период закрыт.";
            }
            if (own is null && !l.IsOpenForGrade(GradeOn(l)))
            {
                return "Запись на это занятие закрыта.";
            }
            if (l.Enrollments.Count(e => e.Status == EnrollmentStatus.Active) >= l.Capacity)
            {
                return NoSeats;
            }
            var end = l.StartsAt.AddMinutes(l.DurationMinutes);
            var overlap = Busy.FirstOrDefault(b => b.LessonId != l.Id && b.StartsAt < end && l.StartsAt < b.StartsAt.AddMinutes(b.DurationMinutes));
            return overlap is null ? null : $"В это время другое занятие: {overlap.Subject}, {SchoolTime.ToSchool(overlap.StartsAt):H:mm}.";
        }
    }
}
