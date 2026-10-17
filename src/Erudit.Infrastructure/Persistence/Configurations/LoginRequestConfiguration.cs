using Erudit.Domain.People;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Metadata.Builders;

namespace Erudit.Infrastructure.Persistence.Configurations;

public class LoginRequestConfiguration : IEntityTypeConfiguration<LoginRequest>
{
    public void Configure(EntityTypeBuilder<LoginRequest> b)
    {
        b.Property(r => r.Token).HasMaxLength(64);
        b.Property(r => r.SecretHash).HasMaxLength(64);
        b.Property(r => r.DeviceInfo).HasMaxLength(100);
        b.HasIndex(r => r.Token).IsUnique();
        b.HasIndex(r => r.CreatedAt); // для будущей очистки старых запросов
    }
}
