using Gamex.Notifications.Application.Notifications;
using Gamex.Notifications.Domain.Notifications;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Storage;

namespace Gamex.Notifications.Application.Abstractions;

public interface IAppDbContext
{
    DbSet<Notification> Notifications { get; }

    Task<int> SaveChangesAsync(CancellationToken cancellationToken = default);

    Task<IDbContextTransaction> BeginTransactionAsync(CancellationToken cancellationToken);

    bool IsUniqueViolation(DbUpdateException exception);

    void DiscardChanges();
}

/// <summary>Server → client pushes over SignalR (group <c>user:{userId}</c>).</summary>
public interface IRealtimeNotifier
{
    Task NotificationReceivedAsync(Guid userId, NotificationDto notification, CancellationToken cancellationToken);

    Task UnreadCountUpdatedAsync(Guid userId, int count, CancellationToken cancellationToken);
}
