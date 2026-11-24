using Microsoft.EntityFrameworkCore;

namespace FinFlow.Audit.Api;

public static class AuditQueries
{
    public static async Task<IResult> ListLifecycle(string? entityId, int? page, AuditDb db, CancellationToken token)
    {
        var pageNumber = page ?? 1;
        if (pageNumber is < 1 or > 1000)
            return Results.Problem(statusCode: 400, detail: "Page must be between 1 and 1000.");

        var query = db.WorkflowEvents.AsNoTracking();
        if (!string.IsNullOrEmpty(entityId))
            query = query.Where(entry => entry.EntityId == entityId);
        var rows = await query.OrderByDescending(entry => entry.OccurredAt).ThenByDescending(entry => entry.EventId)
            .Skip((pageNumber - 1) * 25).Take(25).ToListAsync(token);
        return Results.Ok(rows);
    }

    public static async Task<IResult> ListTransfers(Guid? transferId, int? size, Guid? before,
        AuditDb db, CancellationToken token)
    {
        var limit = size ?? 25;
        if (limit < 1 || limit > 100)
            return Results.Problem(statusCode: 400, detail: "Page size must be between 1 and 100.");

        var query = db.TransferEvents.AsNoTracking();
        if (transferId.HasValue)
            query = query.Where(entry => entry.TransferId == transferId.Value);

        if (before.HasValue)
        {
            var cursor = await query.SingleOrDefaultAsync(entry => entry.EventId == before.Value, token);
            if (cursor is null)
                return Results.NotFound();

            query = query.Where(entry => EF.Functions.LessThan(
                ValueTuple.Create(entry.CompletedAt, entry.EventId),
                ValueTuple.Create(cursor.CompletedAt, cursor.EventId)));
        }

        var rows = await query.OrderByDescending(entry => entry.CompletedAt).ThenByDescending(entry => entry.EventId)
            .Take(limit + 1).ToListAsync(token);
        Guid? next = null;
        if (rows.Count > limit)
        {
            rows.RemoveAt(limit);
            next = rows[limit - 1].EventId;
        }

        return Results.Ok(new { items = rows, next });
    }
}

