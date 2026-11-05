using Erudit.Domain;
using Erudit.Domain.Lessons;
using Erudit.Domain.Notifications;
using Erudit.Domain.People;
using Erudit.Infrastructure.Max;
using Erudit.Infrastructure.Persistence;
using Microsoft.EntityFrameworkCore;

namespace Erudit.Infrastructure.Notifications;

/// <summary>
/// Команды бота «Сегодня» и «На неделю» (этап 6, шаг 5, решено 2026-11-05). Один ответ по всем ролям человека:
/// «Вы ведёте» (учитель), «Вы записаны» (ученик), по разделу на каждого ребёнка (родитель); пустые разделы не показываем.
/// Показываем запланированные, проведённые, не состоявшиеся и отменённые (с пометкой — чтобы не пришли зря).
/// Учителю в «Сегодня» — имена записанных, в «На неделю» — только «записано N из M».
/// В «Сегодня» у ещё не начавшихся занятий ученика — кнопка «Не смогу» (как в напоминаниях).
/// </summary>
public sealed class BotSchedule(EruditDbContext db, TimeProvider time)
{
    /// <summary>Меню команд бота (PATCH /me/commands); имена — латиницей и без «/».</summary>
    public static readonly IReadOnlyList<MaxBotCommand> Commands =
    [
        new("today", "Занятия на сегодня"),
        new("week", "Занятия на неделю"),
    ];

    /// <summary>
    /// Команда ли это: «/today», «/week» (с именем бота или без), а также набранные словами «сегодня», «неделя», «на неделю».
    /// </summary>
    public static bool TryParse(string? text, out bool week)
    {
        week = false;
        var command = (text ?? "").Trim().TrimStart('/').ToLowerInvariant();
        var at = command.IndexOf('@'); // «/today@имя_бота»
        if (at >= 0)
        {
            command = command[..at];
        }
        switch (command.Trim())
        {
            case "today" or "сегодня":
                return true;
            case "week" or "неделя" or "неделю" or "на неделю":
                week = true;
                return true;
            default:
                return false;
        }
    }

    /// <summary>«На неделю»: с сегодняшнего дня до воскресенья; в субботу и воскресенье — вся следующая неделя.</summary>
    public static (DateOnly From, DateOnly To) WeekRange(DateOnly today)
    {
        var from = today.DayOfWeek switch
        {
            DayOfWeek.Saturday => today.AddDays(2),
            DayOfWeek.Sunday => today.AddDays(1),
            _ => today,
        };
        return (from, from.AddDays((7 - (int)from.DayOfWeek) % 7));
    }

    public async Task<MaxMessage> BuildAsync(int personId, bool week, CancellationToken ct = default)
    {
        var nowUtc = time.GetUtcNow().UtcDateTime;
        var today = DateOnly.FromDateTime(SchoolTime.ToSchool(nowUtc));
        var (from, to) = week ? WeekRange(today) : (today, today);
        var (fromUtc, toUtc) = (SchoolTime.ToUtc(from, TimeOnly.MinValue), SchoolTime.ToUtc(to.AddDays(1), TimeOnly.MinValue));
        var title = week ? NotificationText.WeekTitle(from, to) : NotificationText.TodayTitle(today);
        var sections = new List<ScheduleSection>();
        var buttons = new List<NotificationButton>();

        // ---- Учитель: свои занятия
        var taught = await db.Lessons
            .Where(l => l.TeacherId == personId && l.StartsAt >= fromUtc && l.StartsAt < toUtc)
            .OrderBy(l => l.StartsAt)
            .Select(l => new
            {
                l.Status,
                l.Capacity,
                Lesson = new LessonRow(l.StartsAt, l.DurationMinutes, l.LessonNumber, l.Subject.Name, l.Room.Name,
                    l.Teacher.LastName, l.Teacher.FirstName, l.Teacher.MiddleName),
                Students = l.Enrollments.Where(e => e.Status == EnrollmentStatus.Active)
                    .Select(e => new { e.Student.LastName, e.Student.FirstName, e.Student.MiddleName }).ToList(),
            })
            .ToListAsync(ct);
        if (taught.Count > 0)
        {
            sections.Add(new ScheduleSection("Вы ведёте", taught.Select(l =>
            {
                var line = NotificationText.StatusMark(l.Status) + NotificationText.LessonLine(l.Lesson.Info, withDate: week, withTeacher: false);
                if (l.Status == LessonStatus.Cancelled)
                {
                    return line;
                }
                if (week)
                {
                    return $"{line}, записано {l.Students.Count} из {l.Capacity}";
                }
                var names = l.Students.Select(s => Person.Short(s.LastName, s.FirstName, s.MiddleName)).Order().ToList();
                return names.Count == 0
                    ? $"{line}. Пока никто не записан"
                    : $"{line}. Записаны ({names.Count} из {l.Capacity}): {string.Join(", ", names).TrimEnd('.')}";
            }).ToList()));
        }

        // ---- Ученик (сам) и дети родителя
        var children = await db.ParentLinks
            .Where(l => l.ParentId == personId && l.Student.IsActive)
            .OrderBy(l => l.Student.LastName).ThenBy(l => l.Student.FirstName)
            .Select(l => new { l.StudentId, Name = l.Student.LastName + " " + l.Student.FirstName, First = l.Student.FirstName })
            .ToListAsync(ct);
        var studentIds = children.Select(c => c.StudentId).Append(personId).ToList();
        var enrolled = await db.Enrollments
            .Where(e => studentIds.Contains(e.StudentId) && e.Status == EnrollmentStatus.Active
                        && e.Lesson.StartsAt >= fromUtc && e.Lesson.StartsAt < toUtc)
            .OrderBy(e => e.Lesson.StartsAt)
            .Select(e => new
            {
                e.StudentId,
                e.LessonId,
                e.Lesson.Status,
                Lesson = new LessonRow(e.Lesson.StartsAt, e.Lesson.DurationMinutes, e.Lesson.LessonNumber, e.Lesson.Subject.Name, e.Lesson.Room.Name,
                    e.Lesson.Teacher.LastName, e.Lesson.Teacher.FirstName, e.Lesson.Teacher.MiddleName),
            })
            .ToListAsync(ct);

        void AddStudent(int studentId, string sectionTitle, string? childFirst)
        {
            var mine = enrolled.Where(e => e.StudentId == studentId).ToList();
            if (mine.Count == 0)
            {
                return;
            }
            sections.Add(new ScheduleSection(sectionTitle, mine
                .Select(e => NotificationText.StatusMark(e.Status) + NotificationText.LessonLine(e.Lesson.Info, withDate: week))
                .ToList()));
            if (!week)
            {
                buttons.AddRange(mine
                    .Where(e => e.Status == LessonStatus.Planned && e.Lesson.StartsAt > nowUtc)
                    .Select(e => LessonButtons.SkipButton(e.LessonId, studentId, childFirst, e.Lesson.StartsAt, e.Lesson.Subject)));
            }
        }

        AddStudent(personId, "Вы записаны", null);
        foreach (var child in children)
        {
            AddStudent(child.StudentId, child.Name, child.First);
        }

        return LessonButtons.ToMessage(NotificationText.Schedule(title, sections), buttons);
    }
}
