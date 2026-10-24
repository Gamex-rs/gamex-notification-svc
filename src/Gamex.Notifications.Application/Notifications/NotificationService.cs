using System.Text.Json;
using Gamex.Notifications.Application.Abstractions;
using Gamex.Notifications.Application.Common;
using Gamex.Notifications.Domain;
using Gamex.Notifications.Domain.Notifications;
using Microsoft.EntityFrameworkCore;

namespace Gamex.Notifications.Application.Notifications;

/// <summary>SPEC-notification §3: <c>data</c> is the type-specific payload.</summary>
public sealed record NotificationDto(Guid Id, string Type, bool Read, DateTime CreatedAt, JsonElement Data);

public sealed record CursorPage<T>(IReadOnlyList<T> Items, string? NextCursor);

public sealed record UnreadCountDto(int Count);

public interface INotificationService
{
    Task<OperationResult<CursorPage<NotificationDto>>> ListAsync(Guid userId, string? cursor, int? pageSize, CancellationToken ct);

    Task<UnreadCountDto> UnreadCountAsync(Guid userId, CancellationToken ct);

    Task<OperationResult<bool>> MarkReadAsync(Guid userId, Guid notificationId, CancellationToken ct);

    Task MarkAllReadAsync(Guid userId, CancellationToken ct);
}

internal sealed class NotificationService(IAppDbContext db, IRealtimeNotifier realtime, TimeProvider time) : INotificationService
{
    public const int DefaultPageSize = 20;
    public const int MaxPageSize = 50;

    public async Task<OperationResult<CursorPage<NotificationDto>>> ListAsync(Guid userId, string? cursor, int? pageSize, CancellationToken ct)
    {
        if (pageSize is < 1 or > MaxPageSize)
        {
            return OperationResult<CursorPage<NotificationDto>>.Invalid("pageSize", $"Must be between 1 and {MaxPageSize}.");
        }

        var query = db.Notifications.AsNoTracking().Where(n => n.UserId == userId);
        if (cursor is not null)
        {
            if (!Cursor.TryDecode(cursor, out var at, out var lastId))
            {
                return OperationResult<CursorPage<NotificationDto>>.Invalid("cursor", "Invalid cursor.");
            }

            var last = lastId.ToString();
#pragma warning disable CA1309 // Translated to SQL by EF; the StringComparison overload is not translatable.
            query = query.Where(n => n.CreatedAt < at || (n.CreatedAt == at && string.Compare(n.Id.ToString(), last) < 0));
#pragma warning restore CA1309
        }

        var size = pageSize ?? DefaultPageSize;
        var page = await query.OrderByDescending(n => n.CreatedAt).ThenByDescending(n => n.Id).Take(size + 1).ToListAsync(ct);
        var items = page.Take(size).Select(ToDto).ToList();
        var next = page.Count > size ? Cursor.Encode(items[^1].CreatedAt, items[^1].Id) : null;
        return OperationResult<CursorPage<NotificationDto>>.Ok(new CursorPage<NotificationDto>(items, next));
    }

    public async Task<UnreadCountDto> UnreadCountAsync(Guid userId, CancellationToken ct) =>
        new(await db.Notifications.CountAsync(n => n.UserId == userId && n.ReadAt == null, ct));

    public async Task<OperationResult<bool>> MarkReadAsync(Guid userId, Guid notificationId, CancellationToken ct)
    {
        var now = Timestamps.ToStoragePrecision(time.GetUtcNow().UtcDateTime);

        // Scoped to the caller: someone else's notification is indistinguishable from a missing one.
        if (!await db.Notifications.AnyAsync(n => n.Id == notificationId && n.UserId == userId, ct))
        {
            return OperationResult<bool>.NotFound($"No notification with id {notificationId}.");
        }

        var updated = await db.Notifications
            .Where(n => n.Id == notificationId && n.UserId == userId && n.ReadAt == null)
            .ExecuteUpdateAsync(s => s.SetProperty(n => n.ReadAt, now), ct);
        if (updated > 0)
        {
            await PushCountAsync(userId, ct);
        }

        return OperationResult<bool>.Ok(true);
    }

    public async Task MarkAllReadAsync(Guid userId, CancellationToken ct)
    {
        var now = Timestamps.ToStoragePrecision(time.GetUtcNow().UtcDateTime);
        var updated = await db.Notifications
            .Where(n => n.UserId == userId && n.ReadAt == null)
            .ExecuteUpdateAsync(s => s.SetProperty(n => n.ReadAt, now), ct);
        if (updated > 0)
        {
            await PushCountAsync(userId, ct);
        }
    }

    private async Task PushCountAsync(Guid userId, CancellationToken ct) =>
        await realtime.UnreadCountUpdatedAsync(userId, (await UnreadCountAsync(userId, ct)).Count, ct);

    internal static NotificationDto ToDto(Notification n)
    {
        using var payload = JsonDocument.Parse(n.Payload);
        return new NotificationDto(n.Id, n.Type, n.IsRead, n.CreatedAt, payload.RootElement.Clone());
    }
}
