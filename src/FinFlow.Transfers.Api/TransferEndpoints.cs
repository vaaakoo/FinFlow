using System.Security.Claims;
using Microsoft.EntityFrameworkCore;

namespace FinFlow.Transfers.Api;

public static class TransferEndpoints
{
    public static async Task<IResult> Submit(HttpRequest http, ClaimsPrincipal user, TransferInput input,
        TransfersDb db, CancellationToken token)
    {
        var keys = http.Headers["Idempotency-Key"];
        var key = keys.ToString();
        if (keys.Count != 1 || key.Length is < 1 or > 128 || key.Any(c => c < '!' || c > '~' || c == ','))
            return Results.Problem(statusCode: 400, detail: "Supply one valid Idempotency-Key.");
        if (input.AmountMinor <= 0 || input.SourceAccountId == Guid.Empty || input.DestinationAccountId == Guid.Empty
            || input.SourceAccountId == input.DestinationAccountId)
            return Results.Problem(statusCode: 400, detail: "Use distinct account IDs and a positive amount.");

        var subject = user.FindFirst("sub")!.Value;
        var id = Guid.NewGuid();
        var now = DateTimeOffset.UtcNow;
        await db.Database.ExecuteSqlInterpolatedAsync($"""
            INSERT INTO "Orders" ("Id", "Subject", "Key", "SourceAccountId", "DestinationAccountId",
                "AmountMinor", "CreatedAt", "Status", "Attempts", "NextAttempt")
            VALUES ({id}, {subject}, {key}, {input.SourceAccountId}, {input.DestinationAccountId},
                {input.AmountMinor}, {now}, 'Pending', 0, {now})
            ON CONFLICT ("Subject", "Key") DO NOTHING
            """, token);

        var order = await db.Orders.AsNoTracking().SingleAsync(o => o.Subject == subject && o.Key == key, token);
        if (order.AmountMinor != input.AmountMinor || order.SourceAccountId != input.SourceAccountId
            || order.DestinationAccountId != input.DestinationAccountId)
            return Results.Problem(statusCode: 409, detail: "Idempotency key was used with another payload.");

        return Results.Accepted($"/customers/me/transfers/{order.Id}", order.View());
    }

    public static async Task<IResult> Get(Guid id, ClaimsPrincipal user, TransfersDb db, CancellationToken token)
    {
        var subject = user.FindFirst("sub")!.Value;
        var order = await db.Orders.AsNoTracking()
            .SingleOrDefaultAsync(o => o.Id == id && o.Subject == subject, token);
        if (order is null)
            return Results.NotFound();

        return Results.Ok(order.View());
    }

    public static async Task<IResult> List(ClaimsPrincipal user, int? size, Guid? before, TransfersDb db,
        CancellationToken token)
    {
        var limit = size ?? 25;
        if (limit is < 1 or > 100)
            return Results.Problem(statusCode: 400, detail: "Page size must be between 1 and 100.");

        var subject = user.FindFirst("sub")!.Value;
        var query = db.Orders.AsNoTracking().Where(o => o.Subject == subject);
        if (before is { } beforeId)
        {
            var cursor = await query.SingleOrDefaultAsync(o => o.Id == beforeId, token);
            if (cursor is null)
                return Results.NotFound();
            query = query.Where(o => EF.Functions.LessThan(ValueTuple.Create(o.CreatedAt, o.Id),
                ValueTuple.Create(cursor.CreatedAt, cursor.Id)));
        }

        var orders = await query.OrderByDescending(o => o.CreatedAt).ThenByDescending(o => o.Id)
            .Take(limit + 1).ToListAsync(token);
        var items = orders.Take(limit).Select(o => o.View()).ToArray();
        return Results.Ok(new { items, next = orders.Count > limit ? (Guid?)items[^1].Id : null });
    }
}

public sealed record TransferInput(Guid SourceAccountId, Guid DestinationAccountId, long AmountMinor);

