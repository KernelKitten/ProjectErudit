using Erudit.Domain.Accounting;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Metadata.Builders;

namespace Erudit.Infrastructure.Persistence.Configurations;

public class ClosedPeriodConfiguration : IEntityTypeConfiguration<ClosedPeriod>
{
    public void Configure(EntityTypeBuilder<ClosedPeriod> b)
    {
        b.HasIndex(p => p.Month).IsUnique();
        b.ToTable(t => t.HasCheckConstraint("ck_closed_periods_month", "EXTRACT(DAY FROM month) = 1"));
    }
}

public class AuditEntryConfiguration : IEntityTypeConfiguration<AuditEntry>
{
    public void Configure(EntityTypeBuilder<AuditEntry> b)
    {
        b.ToTable("audit_log");
        b.Property(e => e.EntityType).HasMaxLength(50);
        b.Property(e => e.EntityId).HasMaxLength(50);
        b.Property(e => e.Action).HasMaxLength(20);
        // jsonb — тип PostgreSQL для JSON: по нему можно искать, а не только хранить текст.
        b.Property(e => e.Changes).HasColumnType("jsonb");
        b.HasIndex(e => new { e.EntityType, e.EntityId });
        b.HasIndex(e => e.At);
    }
}
