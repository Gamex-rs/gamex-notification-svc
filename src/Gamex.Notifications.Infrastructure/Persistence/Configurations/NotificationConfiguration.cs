using Gamex.Notifications.Domain.Notifications;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Metadata.Builders;

namespace Gamex.Notifications.Infrastructure.Persistence.Configurations;

internal sealed class NotificationConfiguration : IEntityTypeConfiguration<Notification>
{
    public void Configure(EntityTypeBuilder<Notification> builder)
    {
        builder.ToTable("notifications");
        builder.HasKey(n => n.Id);
        builder.Property(n => n.Id).ValueGeneratedNever();
        builder.Property(n => n.Type).HasMaxLength(100).IsRequired();
        builder.Property(n => n.Payload).HasColumnType("json").IsRequired();
        builder.Property(n => n.SourceMessageId).HasMaxLength(100).IsRequired();
        builder.Ignore(n => n.IsRead);

        // SPEC-notification §5.
        builder.HasIndex(n => new { n.UserId, n.CreatedAt });
        builder.HasIndex(n => new { n.UserId, n.ReadAt });

        // An SQS message redelivered after a crash must not notify the same user twice.
        builder.HasIndex(n => new { n.SourceMessageId, n.UserId }).IsUnique();
    }
}
