using System.Text.Json;
using Gamex.Notifications.Application.Abstractions;
using Gamex.Notifications.Domain.Notifications;
using Microsoft.EntityFrameworkCore;

namespace Gamex.Notifications.Application.Notifications;

/// <summary>A notification to create for one recipient; the payload is serialized as the notification's <c>data</c>.</summary>
public sealed record NotificationRequest(Guid UserId, string Type, object Payload);

public enum HandleOutcome
{
    Created,

    /// <summary>The SQS message was already processed (redelivery); nothing was created.</summary>
    Duplicate,
}

public interface IActivityEventHandler
{
    /// <summary>Creates the notifications of one SQS message atomically and pushes them to connected clients.</summary>
    Task<HandleOutcome> HandleAsync(string sourceMessageId, IReadOnlyList<NotificationRequest> requests, CancellationToken ct);
}

internal sealed class ActivityEventHandler(IAppDbContext db, IRealtimeNotifier realtime, TimeProvider time) : IActivityEventHandler
{
    private static readonly JsonSerializerOptions Json = new(JsonSerializerDefaults.Web);

    public async Task<HandleOutcome> HandleAsync(string sourceMessageId, IReadOnlyList<NotificationRequest> requests, CancellationToken ct)
    {
        var now = time.GetUtcNow().UtcDateTime;
        var notifications = requests
            .Select(r => Notification.Create(r.UserId, r.Type, JsonSerializer.Serialize(r.Payload, Json), sourceMessageId, now))
            .ToList();

        db.Notifications.AddRange(notifications);
        try
        {
            // One SaveChanges = one transaction: either every recipient of the message gets it or none does.
            await db.SaveChangesAsync(ct);
        }
        catch (DbUpdateException ex) when (db.IsUniqueViolation(ex))
        {
            db.DiscardChanges();
            return HandleOutcome.Duplicate;
        }

        foreach (var notification in notifications)
        {
            await realtime.NotificationReceivedAsync(notification.UserId, NotificationService.ToDto(notification), ct);
            var unread = await db.Notifications.CountAsync(n => n.UserId == notification.UserId && n.ReadAt == null, ct);
            await realtime.UnreadCountUpdatedAsync(notification.UserId, unread, ct);
        }

        return HandleOutcome.Created;
    }
}
