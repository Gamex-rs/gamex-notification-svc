using System.Text.Json;
using Gamex.Notifications.Application.Notifications;
using Gamex.Notifications.Domain.Notifications;
using Gamex.Notifications.Infrastructure.Messaging;

namespace Gamex.Notifications.UnitTests;

public sealed class EventMappingTests
{
    private static readonly Guid A = Guid.Parse("0192f3a1-0000-7000-8000-00000000000a");
    private static readonly Guid B = Guid.Parse("0192f3a1-0000-7000-8000-00000000000b");
    private static readonly Guid X = Guid.Parse("0192f3a1-0000-7000-8000-0000000000f1");

    private static JsonElement Data(NotificationRequest request) => JsonSerializer.SerializeToElement(request.Payload);

    [Fact]
    public void MessageSent_NotifiesRecipient()
    {
        var mapped = ActivityEventsConsumer.Map($$"""
            {"eventType":"message.sent","conversationId":"{{X}}","messageId":"{{X}}","senderId":"{{A}}","senderName":"Marko P.","recipientId":"{{B}}","listingId":"{{X}}","gameTitle":"The Last of Us Part II","occurredAt":"2026-10-24T10:00:00Z"}
            """);

        var request = mapped.Requests.ShouldNotBeNull().ShouldHaveSingleItem();
        request.UserId.ShouldBe(B);
        request.Type.ShouldBe(NotificationTypes.NewMessage);
        var data = Data(request);
        data.GetProperty("conversationId").GetGuid().ShouldBe(X);
        data.GetProperty("senderName").GetString().ShouldBe("Marko P.");
        data.GetProperty("gameTitle").GetString().ShouldBe("The Last of Us Part II");
    }

    [Fact]
    public void ListingCreated_NotifiesWishlistUser()
    {
        var mapped = ActivityEventsConsumer.Map($$"""
            {"eventType":"listing.created","listingId":"{{X}}","gameId":"{{X}}","gameTitle":"Hades","userId":"{{A}}","sellerName":"Ana","listingType":"sale","platformId":"{{X}}","platformSlug":"pc","format":"physical","notifyUserId":"{{B}}","occurredAt":"2026-10-24T10:00:00Z"}
            """);

        var request = mapped.Requests.ShouldNotBeNull().ShouldHaveSingleItem();
        request.UserId.ShouldBe(B);
        request.Type.ShouldBe(NotificationTypes.WishlistMatch);
        Data(request).GetProperty("listingType").GetString().ShouldBe("sale");
        Data(request).GetProperty("sellerName").GetString().ShouldBe("Ana");
    }

    [Fact]
    public void ListingExpired_NotifiesOwner()
    {
        var mapped = ActivityEventsConsumer.Map($$"""
            {"eventType":"listing.expired","listingId":"{{X}}","gameTitle":"Tunic","userId":"{{A}}","expiresAt":"2026-10-24T09:00:00Z","occurredAt":"2026-10-24T10:00:00Z"}
            """);

        var request = mapped.Requests.ShouldNotBeNull().ShouldHaveSingleItem();
        request.UserId.ShouldBe(A);
        request.Type.ShouldBe(NotificationTypes.ListingExpiring);
        Data(request).GetProperty("gameTitle").GetString().ShouldBe("Tunic");
    }

    [Fact]
    public void TransactionCompleted_AsksBothPartiesToRateTheOther()
    {
        var mapped = ActivityEventsConsumer.Map($$"""
            {"eventType":"transaction.completed","transactionId":"{{X}}","listingEntryId":"{{X}}","gameTitle":"Gris","userIdA":"{{A}}","userNameA":"Ana","userIdB":"{{B}}","userNameB":"Boris","occurredAt":"2026-10-24T10:00:00Z"}
            """);

        var requests = mapped.Requests.ShouldNotBeNull();
        requests.Count.ShouldBe(2);
        requests.ShouldAllBe(r => r.Type == NotificationTypes.RateYourPartner);
        Data(requests.Single(r => r.UserId == A)).GetProperty("partnerName").GetString().ShouldBe("Boris");
        Data(requests.Single(r => r.UserId == B)).GetProperty("partnerUserId").GetGuid().ShouldBe(A);
    }

    [Fact]
    public void UnknownType_IsSkippedNotInvalid()
    {
        var mapped = ActivityEventsConsumer.Map("""{"eventType":"profile.updated","userId":"x"}""");

        mapped.IsUnknownType.ShouldBeTrue();
        mapped.IsInvalid.ShouldBeFalse();
    }

    [Theory]
    [InlineData("not json")]
    [InlineData("""{"conversationId":"x"}""")]
    [InlineData("""{"eventType":"message.sent","conversationId":"not-a-guid"}""")]
    [InlineData("""{"eventType":"message.sent","conversationId":"0192f3a1-0000-7000-8000-0000000000f1"}""")]
    public void Malformed_IsInvalid(string body)
    {
        ActivityEventsConsumer.Map(body).IsInvalid.ShouldBeTrue();
    }

    [Fact]
    public void LeadingBom_IsTolerated()
    {
        var mapped = ActivityEventsConsumer.Map("﻿" + $$"""{"eventType":"listing.expired","listingId":"{{X}}","gameTitle":"Tunic","userId":"{{A}}"}""");

        mapped.Requests.ShouldNotBeNull().ShouldHaveSingleItem();
    }
}
