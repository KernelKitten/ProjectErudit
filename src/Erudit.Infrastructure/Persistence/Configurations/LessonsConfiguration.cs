using Erudit.Domain.Calendar;
using Erudit.Domain.Lessons;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Metadata.Builders;

namespace Erudit.Infrastructure.Persistence.Configurations;

public class CalendarDayConfiguration : IEntityTypeConfiguration<CalendarDay>
{
    public void Configure(EntityTypeBuilder<CalendarDay> b)
    {
        b.HasKey(d => d.Date);
    }
}

public class VacationConfiguration : IEntityTypeConfiguration<Vacation>
{
    public void Configure(EntityTypeBuilder<Vacation> b)
    {
        b.Property(v => v.Name).HasMaxLength(100);
        b.ToTable(t => t.HasCheckConstraint("ck_vacations_dates", "end_date >= start_date"));
    }
}

public class SeriesConfiguration : IEntityTypeConfiguration<Series>
{
    public void Configure(EntityTypeBuilder<Series> b)
    {
        b.Property(s => s.Topic).HasMaxLength(200);
        b.Property(s => s.Comment).HasMaxLength(1000);
        b.HasIndex(s => s.TeacherId);
        b.ToTable(t =>
        {
            t.HasCheckConstraint("ck_series_dates", "to_date >= from_date");
            t.HasCheckConstraint("ck_series_capacity", "capacity >= 1");
            t.HasCheckConstraint("ck_series_duration", "duration_minutes > 0");
        });
    }
}

public class SeriesEnrollmentConfiguration : IEntityTypeConfiguration<SeriesEnrollment>
{
    public void Configure(EntityTypeBuilder<SeriesEnrollment> b)
    {
        b.HasIndex(e => new { e.SeriesId, e.StudentId }).IsUnique();
    }
}

public class LessonConfiguration : IEntityTypeConfiguration<Lesson>
{
    public void Configure(EntityTypeBuilder<Lesson> b)
    {
        b.Property(l => l.Topic).HasMaxLength(200);
        b.Property(l => l.Comment).HasMaxLength(1000);
        b.HasOne(l => l.Series).WithMany(s => s.Lessons).HasForeignKey(l => l.SeriesId);

        // Самые частые запросы: «занятия учителя за период» и «занятия на сегодня».
        b.HasIndex(l => new { l.TeacherId, l.StartsAt });
        b.HasIndex(l => l.StartsAt);
        b.HasIndex(l => l.SeriesId);

        b.Ignore(l => l.ReportedMinutes);
        b.ToTable(t =>
        {
            t.HasCheckConstraint("ck_lessons_capacity", "capacity >= 1");
            t.HasCheckConstraint("ck_lessons_duration", "duration_minutes > 0");
        });
    }
}

public class EnrollmentConfiguration : IEntityTypeConfiguration<Enrollment>
{
    public void Configure(EntityTypeBuilder<Enrollment> b)
    {
        // Ученик записан на занятие не больше одного раза (отказ — это смена статуса, а не новая строка).
        b.HasIndex(e => new { e.LessonId, e.StudentId }).IsUnique();
        b.HasIndex(e => e.StudentId);
    }
}
