using Erudit.Domain.School;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Metadata.Builders;

namespace Erudit.Infrastructure.Persistence.Configurations;

public class AcademicYearConfiguration : IEntityTypeConfiguration<AcademicYear>
{
    public void Configure(EntityTypeBuilder<AcademicYear> b)
    {
        b.Property(y => y.Name).HasMaxLength(20);
        b.HasIndex(y => y.Name).IsUnique();
    }
}

public class SchoolClassConfiguration : IEntityTypeConfiguration<SchoolClass>
{
    public void Configure(EntityTypeBuilder<SchoolClass> b)
    {
        b.Property(c => c.Letter).HasMaxLength(5);
        b.HasIndex(c => new { c.AcademicYearId, c.Grade, c.Letter }).IsUnique();
        b.HasOne(c => c.ClassTeacher).WithMany().HasForeignKey(c => c.ClassTeacherId);
        b.Ignore(c => c.Name);
        b.ToTable(t => t.HasCheckConstraint("ck_school_classes_grade", "grade BETWEEN 0 AND 11"));
    }
}

public class ClassMembershipConfiguration : IEntityTypeConfiguration<ClassMembership>
{
    public void Configure(EntityTypeBuilder<ClassMembership> b)
    {
        b.HasIndex(m => new { m.StudentId, m.FromDate });
        b.HasIndex(m => m.SchoolClassId);
        // Не больше одного «открытого» класса у ученика.
        b.HasIndex(m => m.StudentId).IsUnique().HasFilter("to_date IS NULL")
            .HasDatabaseName("ix_class_memberships_student_id_current");
        b.ToTable(t => t.HasCheckConstraint("ck_class_memberships_dates", "to_date IS NULL OR to_date >= from_date"));
    }
}

public class SubjectConfiguration : IEntityTypeConfiguration<Subject>
{
    public void Configure(EntityTypeBuilder<Subject> b)
    {
        b.Property(s => s.Name).HasMaxLength(100);
        b.Property(s => s.ShortName).HasMaxLength(30);
        b.HasIndex(s => s.Name).IsUnique();
    }
}

public class RoomConfiguration : IEntityTypeConfiguration<Room>
{
    public void Configure(EntityTypeBuilder<Room> b)
    {
        b.Property(r => r.Name).HasMaxLength(50);
        b.HasIndex(r => r.Name).IsUnique();
    }
}

public class LessonPeriodConfiguration : IEntityTypeConfiguration<LessonPeriod>
{
    public void Configure(EntityTypeBuilder<LessonPeriod> b)
    {
        b.HasKey(p => p.Number);
        // Номер урока задаём сами (0–9), а не база.
        b.Property(p => p.Number).ValueGeneratedNever();
        b.Ignore(p => p.DurationMinutes);
        b.ToTable(t =>
        {
            t.HasCheckConstraint("ck_lesson_periods_number", "number BETWEEN 0 AND 9");
            t.HasCheckConstraint("ck_lesson_periods_time", "\"end\" > start");
        });
    }
}
