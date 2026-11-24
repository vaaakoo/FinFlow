using System.Security.Claims;
using Microsoft.AspNetCore.SignalR;
using Microsoft.EntityFrameworkCore;

namespace FinFlow.Notifications.Worker;

public sealed class NotificationHub : Hub { }

public sealed class NotificationUserIdProvider : IUserIdProvider
{
    public string? GetUserId(HubConnectionContext connection) => connection.User?.FindFirst("sub")?.Value;
}

public static class NotificationEndpoints
{
    public static async Task<IResult> List(int? page, ClaimsPrincipal user, NotificationsDb db, CancellationToken token)
    {
        var pageNumber = page ?? 1;
        if (pageNumber is < 1 or > 1000)
            return Results.Problem(statusCode: 400, detail: "Page must be between 1 and 1000.");

        var subject = user.FindFirst("sub")!.Value;
        var rows = await db.Notifications.AsNoTracking().Where(n => n.RecipientSubject == subject)
            .OrderByDescending(n => n.CreatedAt).ThenByDescending(n => n.EventId)
            .Skip((pageNumber - 1) * 25).Take(25).ToListAsync(token);
        return Results.Ok(rows.Select(Entry));
    }

    public static async Task<IResult> MarkRead(Guid id, ClaimsPrincipal user, NotificationsDb db, CancellationToken token)
    {
        var subject = user.FindFirst("sub")!.Value;
        var now = DateTimeOffset.UtcNow;
        var updated = await db.Notifications.Where(n => n.EventId == id && n.RecipientSubject == subject)
            .ExecuteUpdateAsync(setters => setters.SetProperty(n => n.ReadAt, n => n.ReadAt ?? now), token);
        return updated == 0 ? Results.NotFound() : Results.NoContent();
    }

    public static NotificationView Entry(Notification notification) =>
        new(notification.EventId, notification.TransferId, notification.RecipientAccountId,
            notification.AmountMinor, notification.Currency, notification.CreatedAt, notification.ReadAt);
}

public sealed record NotificationView(Guid EventId, Guid TransferId, Guid RecipientAccountId,
    long AmountMinor, string Currency, DateTimeOffset CreatedAt, DateTimeOffset? ReadAt);
