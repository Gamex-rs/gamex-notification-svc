namespace Gamex.Notifications.Domain.Notifications;

public static class NotificationTypes
{
    public const string NewMessage = "new_message";
    public const string WishlistMatch = "wishlist_match";
    public const string ListingExpiring = "listing_expiring";
    public const string RateYourPartner = "rate_your_partner";
}

/// <summary>An in-app notification for one user. <see cref="Payload"/> is type-specific JSON for the frontend.</summary>
public sealed class Notification
{
    private Notification()
    {
        Type = null!;
        Payload = null!;
        SourceMessageId = null!;
    }

    public Guid Id { get; private set; }
    public Guid UserId { get; private set; }
    public string Type { get; private set; }

    /// <summary>JSON object (SPEC-notification §6).</summary>
    public string Payload { get; private set; }

    /// <summary>Null = unread.</summary>
    public DateTime? ReadAt { get; private set; }
    public DateTime CreatedAt { get; private set; }

    /// <summary>SQS message id the notification came from; with <see cref="UserId"/> it makes redelivery idempotent.</summary>
    public string SourceMessageId { get; private set; }

    public bool IsRead => ReadAt is not null;

    public static Notification Create(Guid userId, string type, string payloadJson, string sourceMessageId, DateTime now)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(type);
        ArgumentException.ThrowIfNullOrWhiteSpace(payloadJson);
        return new Notification
        {
            Id = Guid.CreateVersion7(),
            UserId = userId,
            Type = type,
            Payload = payloadJson,
            SourceMessageId = sourceMessageId,
            CreatedAt = Timestamps.ToStoragePrecision(now),
        };
    }
}
