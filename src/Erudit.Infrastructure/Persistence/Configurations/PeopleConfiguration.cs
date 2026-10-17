using Erudit.Domain.People;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Metadata.Builders;

namespace Erudit.Infrastructure.Persistence.Configurations;

public class PersonConfiguration : IEntityTypeConfiguration<Person>
{
    public void Configure(EntityTypeBuilder<Person> b)
    {
        b.Property(p => p.LastName).HasMaxLength(100);
        b.Property(p => p.FirstName).HasMaxLength(100);
        b.Property(p => p.MiddleName).HasMaxLength(100);
        b.Property(p => p.Phone).HasMaxLength(20);

        // Один телефон и один аккаунт MAX — у одного человека.
        // Индекс с фильтром: у многих телефона пока нет, и NULL не должен считаться дублем.
        b.HasIndex(p => p.Phone).IsUnique().HasFilter("phone IS NOT NULL");
        b.HasIndex(p => p.MaxUserId).IsUnique().HasFilter("max_user_id IS NOT NULL");
        b.HasIndex(p => new { p.LastName, p.FirstName });

        // Вычисляемые свойства — не колонки.
        b.Ignore(p => p.FullName);
        b.Ignore(p => p.ShortName);
    }
}

public class PersonRoleConfiguration : IEntityTypeConfiguration<PersonRole>
{
    public void Configure(EntityTypeBuilder<PersonRole> b)
    {
        // Составной ключ: одна и та же роль у человека не может быть дважды.
        b.HasKey(r => new { r.PersonId, r.Role });
        b.HasOne(r => r.Person).WithMany(p => p.Roles).HasForeignKey(r => r.PersonId)
            .OnDelete(DeleteBehavior.Cascade);
    }
}

public class ParentLinkConfiguration : IEntityTypeConfiguration<ParentLink>
{
    public void Configure(EntityTypeBuilder<ParentLink> b)
    {
        b.HasKey(l => new { l.ParentId, l.StudentId });
        b.HasOne(l => l.Parent).WithMany().HasForeignKey(l => l.ParentId);
        b.HasOne(l => l.Student).WithMany().HasForeignKey(l => l.StudentId);
        b.HasIndex(l => l.StudentId);
    }
}
