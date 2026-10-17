using Erudit.Domain.Calendar;

namespace Erudit.Domain.Lessons;

/// <summary>
/// Превращает правило серии в конкретные занятия.
/// Чистая логика без базы данных — главный кандидат на тесты.
/// </summary>
public static class SeriesPlanner
{
    /// <summary>
    /// Даты занятий серии: каждый <see cref="Series.DayOfWeek"/> с FromDate по ToDate включительно,
    /// кроме нерабочих дней.
    /// </summary>
    public static IReadOnlyList<DateOnly> GetLessonDates(Series series, WorkingDayCalendar calendar)
    {
        if (series.ToDate < series.FromDate)
        {
            throw new ArgumentException("Дата окончания серии раньше даты начала.", nameof(series));
        }

        // Первый подходящий день недели, начиная с FromDate.
        // Пример: FromDate — понедельник (1), нужна среда (3): сдвиг (3 - 1 + 7) % 7 = 2 дня.
        var shift = ((int)series.DayOfWeek - (int)series.FromDate.DayOfWeek + 7) % 7;
        var dates = new List<DateOnly>();
        for (var date = series.FromDate.AddDays(shift); date <= series.ToDate; date = date.AddDays(7))
        {
            if (calendar.IsWorkingDay(date))
            {
                dates.Add(date);
            }
        }
        return dates;
    }

    /// <summary>Создаёт занятия серии (ещё не сохранённые в базу).</summary>
    public static List<Lesson> CreateLessons(Series series, WorkingDayCalendar calendar) =>
        GetLessonDates(series, calendar)
            .Select(date => new Lesson
            {
                Series = series,
                SeriesId = series.Id,
                TeacherId = series.TeacherId,
                SubjectId = series.SubjectId,
                RoomId = series.RoomId,
                StartsAt = SchoolTime.ToUtc(date, series.StartTime),
                DurationMinutes = series.DurationMinutes,
                LessonNumber = series.LessonNumber,
                Capacity = series.Capacity,
                Topic = series.Topic,
                Comment = series.Comment,
            })
            .ToList();
}
