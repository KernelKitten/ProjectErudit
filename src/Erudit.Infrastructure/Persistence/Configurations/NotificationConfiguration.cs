using Erudit.Domain.Notifications;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Metadata.Builders;

namespace Erudit.Infrastructure.Persistence.Configurations;

public class NotificationConfiguration : IEntityTypeConfiguration<Notification>
{
    public void Configure(EntityTypeBuilder<Notification> b)
    {
        // Предел длины сообщения в MAX — 4000 символов.
        b.Property(n => n.Text).HasMaxLength(4000);
        b.Property(n => n.LastError).HasMaxLength(300);
        b.Property(n => n.Key).HasMaxLength(100);
        b.Property(n => n.Buttons).HasColumnType("jsonb");
        b.HasIndex(n => n.Key).IsUnique().HasFilter("key IS NOT NULL");
        // Служба отправки каждые несколько секунд ищет ждущие сообщения; отправленных будет намного больше.
        b.HasIndex(n => n.NextAttemptAt).HasFilter("status = 'Pending'");
        b.HasIndex(n => n.RecipientId);
    }
}
