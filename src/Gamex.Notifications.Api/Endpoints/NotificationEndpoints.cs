using System.Security.Claims;
using Gamex.Notifications.Api.Auth;
using Gamex.Notifications.Application.Common;
using Gamex.Notifications.Application.Notifications;

namespace Gamex.Notifications.Api.Endpoints;

internal static class NotificationEndpoints
{
    private const string TypeBase = "https://gamex.rs/errors/";

    public static IEndpointRouteBuilder MapNotificationEndpoints(this IEndpointRouteBuilder app)
    {
        var notifications = app.MapGroup("/api/v1/notifications").WithTags("Notifications").RequireAuthorization();

        notifications.MapGet("/", async (ClaimsPrincipal user, INotificationService service, CancellationToken ct, string? cursor = null, int? pageSize = null) =>
            ToResult(await service.ListAsync(user.UserId(), cursor, pageSize, ct), Results.Ok));

        notifications.MapGet("/unread-count", async (ClaimsPrincipal user, INotificationService service, CancellationToken ct) =>
            Results.Ok(await service.UnreadCountAsync(user.UserId(), ct)));

        notifications.MapPut("/{id:guid}/read", async (Guid id, ClaimsPrincipal user, INotificationService service, CancellationToken ct) =>
            ToResult(await service.MarkReadAsync(user.UserId(), id, ct), _ => Results.NoContent()));

        notifications.MapPut("/read-all", async (ClaimsPrincipal user, INotificationService service, CancellationToken ct) =>
        {
            await service.MarkAllReadAsync(user.UserId(), ct);
            return Results.NoContent();
        });

        return app;
    }

    private static IResult ToResult<T>(OperationResult<T> result, Func<T, IResult> onOk) => result.Status switch
    {
        OperationStatus.Ok => onOk(result.Value!),
        OperationStatus.Invalid => Results.ValidationProblem(result.Errors!.ToDictionary(), type: TypeBase + "validation", title: "One or more parameters are invalid."),
        OperationStatus.NotFound => Results.Problem(type: TypeBase + "not-found", title: "Not found", detail: result.Detail, statusCode: StatusCodes.Status404NotFound),
        _ => throw new ArgumentOutOfRangeException(nameof(result), result.Status, null),
    };
}
