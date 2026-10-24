using Gamex.Notifications.Api.Auth;
using Gamex.Notifications.Application.Abstractions;
using Gamex.Notifications.Application.Notifications;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.SignalR;

namespace Gamex.Notifications.Api.Hubs;

/// <summary>
/// Receive-only hub (SPEC-notification §4); each connection joins <c>user:{userId}</c>.
/// Running more than one instance needs a SignalR backplane (e.g. Redis) so pushes reach every instance's clients.
/// </summary>
[Authorize]
internal sealed class NotificationsHub : Hub
{
    public static string UserGroup(Guid userId) => $"user:{userId}";

    public override async Task OnConnectedAsync()
    {
        await Groups.AddToGroupAsync(Context.ConnectionId, UserGroup(Context.User!.UserId()));
        await base.OnConnectedAsync();
    }
}

internal sealed class SignalRNotifier(IHubContext<NotificationsHub> hub) : IRealtimeNotifier
{
    public const string NotificationReceived = "NotificationReceived";
    public const string UnreadCountUpdated = "UnreadCountUpdated";

    public Task NotificationReceivedAsync(Guid userId, NotificationDto notification, CancellationToken cancellationToken) =>
        hub.Clients.Group(NotificationsHub.UserGroup(userId)).SendAsync(NotificationReceived, notification, cancellationToken);

    public Task UnreadCountUpdatedAsync(Guid userId, int count, CancellationToken cancellationToken) =>
        hub.Clients.Group(NotificationsHub.UserGroup(userId)).SendAsync(UnreadCountUpdated, new UnreadCountDto(count), cancellationToken);
}
