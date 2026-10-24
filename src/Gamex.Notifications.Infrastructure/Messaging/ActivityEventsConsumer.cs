using System.Text.Json;
using Amazon.SQS;
using Amazon.SQS.Model;
using Gamex.Notifications.Application.Notifications;
using Gamex.Notifications.Domain.Notifications;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;

namespace Gamex.Notifications.Infrastructure.Messaging;

internal sealed class MessagingOptions
{
    /// <summary><c>SQS_ACTIVITY_EVENTS_URL</c>. The consumer does not run when empty.</summary>
    public string? ActivityEventsQueueUrl { get; init; }
}

/// <summary>Result of mapping one event: the notifications to create, or why none are.</summary>
internal sealed record MappedEvent(string? EventType, IReadOnlyList<NotificationRequest>? Requests)
{
    public bool IsInvalid => Requests is null && EventType is not null;
    public bool IsUnknownType => Requests is null && EventType is null;
}

/// <summary>
/// Consumes <c>gamex-activity-events</c> (SPEC-notification §7): long polling, one transaction per message,
/// then SignalR push and delete. Failures leave the message for SQS redelivery; after 3 receives it goes to the DLQ.
/// </summary>
internal sealed partial class ActivityEventsConsumer(
    IAmazonSQS sqs,
    MessagingOptions options,
    IServiceScopeFactory scopes,
    ILogger<ActivityEventsConsumer> logger) : BackgroundService
{
    private static readonly JsonSerializerOptions Json = new(JsonSerializerDefaults.Web);

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        while (!stoppingToken.IsCancellationRequested)
        {
            try
            {
                var response = await sqs.ReceiveMessageAsync(new ReceiveMessageRequest
                {
                    QueueUrl = options.ActivityEventsQueueUrl,
                    MaxNumberOfMessages = 10,
                    WaitTimeSeconds = 20,
                }, stoppingToken);

                foreach (var message in response.Messages ?? [])
                {
                    await HandleAsync(message, stoppingToken);
                }
            }
            catch (OperationCanceledException) when (stoppingToken.IsCancellationRequested)
            {
                return;
            }
#pragma warning disable CA1031 // Keep polling; unhandled messages become visible again and are retried.
            catch (Exception ex)
#pragma warning restore CA1031
            {
                LogPollFailed(logger, ex);
                await Task.Delay(TimeSpan.FromSeconds(5), stoppingToken);
            }
        }
    }

    internal async Task HandleAsync(Message message, CancellationToken ct)
    {
        var mapped = Map(message.Body);
        if (mapped.IsInvalid)
        {
            // Poison message: leave it so the redrive policy moves it to the DLQ for inspection.
            LogInvalid(logger, mapped.EventType!, message.MessageId);
            return;
        }

        if (mapped.Requests is { Count: > 0 } requests)
        {
            await using var scope = scopes.CreateAsyncScope();
            await scope.ServiceProvider.GetRequiredService<IActivityEventHandler>().HandleAsync(message.MessageId, requests, ct);
        }
        else if (mapped.IsUnknownType)
        {
            // Other consumers' events may share the queue in future; skip rather than dead-letter them.
            LogSkipped(logger, message.MessageId);
        }

        await sqs.DeleteMessageAsync(options.ActivityEventsQueueUrl, message.ReceiptHandle, ct);
    }

    /// <summary>Maps an activity event to notifications (SPEC-notification §6–7). Pure, for unit tests.</summary>
    internal static MappedEvent Map(string body)
    {
        JsonElement root;
        string? eventType;
        try
        {
            using var doc = JsonDocument.Parse(body.TrimStart('﻿'));
            root = doc.RootElement.Clone();
            eventType = root.TryGetProperty("eventType", out var t) ? t.GetString() : null;
        }
        catch (JsonException)
        {
            return new MappedEvent("(unparseable)", null);
        }

        if (string.IsNullOrEmpty(eventType))
        {
            return new MappedEvent("(missing eventType)", null);
        }

        Func<JsonElement, NotificationRequest[]?>? mapper = eventType switch
        {
            "message.sent" => e => MessageSent(e.Deserialize<MessageSentEvent>(Json)!),
            "listing.created" => e => ListingCreated(e.Deserialize<ListingCreatedEvent>(Json)!),
            "listing.expired" => e => ListingExpired(e.Deserialize<ListingExpiredEvent>(Json)!),
            "transaction.completed" => e => TransactionCompleted(e.Deserialize<TransactionCompletedEvent>(Json)!),
            _ => null,
        };

        if (mapper is null)
        {
            return new MappedEvent(null, null);
        }

        try
        {
            // A mapper returns null when required ids are missing: the event is invalid.
            return new MappedEvent(eventType, mapper(root));
        }
        catch (JsonException)
        {
            return new MappedEvent(eventType, null);
        }
    }

    private static NotificationRequest[]? MessageSent(MessageSentEvent e) =>
        e.RecipientId == Guid.Empty || e.ConversationId == Guid.Empty
            ? null
            : [new(e.RecipientId, NotificationTypes.NewMessage, new
            {
                conversationId = e.ConversationId,
                senderId = e.SenderId,
                senderName = e.SenderName,
                gameTitle = e.GameTitle,
                listingId = e.ListingId,
            })];

    private static NotificationRequest[]? ListingCreated(ListingCreatedEvent e) =>
        e.NotifyUserId == Guid.Empty || e.ListingId == Guid.Empty
            ? null
            : [new(e.NotifyUserId, NotificationTypes.WishlistMatch, new
            {
                listingId = e.ListingId,
                gameId = e.GameId,
                gameTitle = e.GameTitle,
                sellerName = e.SellerName,
                listingType = e.ListingType,
                platformSlug = e.PlatformSlug,
                format = e.Format,
            })];

    private static NotificationRequest[]? ListingExpired(ListingExpiredEvent e) =>
        e.UserId == Guid.Empty || e.ListingId == Guid.Empty
            ? null
            : [new(e.UserId, NotificationTypes.ListingExpiring, new
            {
                listingId = e.ListingId,
                gameTitle = e.GameTitle,
                expiresAt = e.ExpiresAt,
            })];

    /// <summary>Both parties are asked to rate the other.</summary>
    private static NotificationRequest[]? TransactionCompleted(TransactionCompletedEvent e) =>
        e.UserIdA == Guid.Empty || e.UserIdB == Guid.Empty
            ? null
            :
            [
                new(e.UserIdA, NotificationTypes.RateYourPartner, new
                {
                    listingId = e.ListingEntryId,
                    transactionId = e.TransactionId,
                    gameTitle = e.GameTitle,
                    partnerUserId = e.UserIdB,
                    partnerName = e.UserNameB,
                }),
                new(e.UserIdB, NotificationTypes.RateYourPartner, new
                {
                    listingId = e.ListingEntryId,
                    transactionId = e.TransactionId,
                    gameTitle = e.GameTitle,
                    partnerUserId = e.UserIdA,
                    partnerName = e.UserNameA,
                }),
            ];

    private sealed record MessageSentEvent(Guid ConversationId, Guid MessageId, Guid SenderId, string? SenderName, Guid RecipientId, Guid ListingId, string? GameTitle);

    private sealed record ListingCreatedEvent(Guid ListingId, Guid GameId, string? GameTitle, Guid UserId, string? SellerName, string? ListingType, string? PlatformSlug, string? Format, Guid NotifyUserId);

    private sealed record ListingExpiredEvent(Guid ListingId, string? GameTitle, Guid UserId, DateTime? ExpiresAt);

    private sealed record TransactionCompletedEvent(Guid TransactionId, Guid ListingEntryId, string? GameTitle, Guid UserIdA, string? UserNameA, Guid UserIdB, string? UserNameB);

    [LoggerMessage(Level = LogLevel.Error, Message = "Polling gamex-activity-events failed")]
    private static partial void LogPollFailed(ILogger logger, Exception exception);

    [LoggerMessage(Level = LogLevel.Error, Message = "Invalid {EventType} event {MessageId}; leaving it for the DLQ")]
    private static partial void LogInvalid(ILogger logger, string eventType, string messageId);

    [LoggerMessage(Level = LogLevel.Warning, Message = "Skipping activity event {MessageId} of an unknown type")]
    private static partial void LogSkipped(ILogger logger, string messageId);
}
