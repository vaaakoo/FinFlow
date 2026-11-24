using Microsoft.EntityFrameworkCore;

namespace FinFlow.Ledger.Api;

public static class Transfers
{
    public static async Task<IResult> Get(Guid id, LedgerDb db, CancellationToken token)
    {
        var transfer = await db.Transfers.FindAsync([id], token);
        if (transfer is null)
            return Results.NotFound();

        return Results.Ok(transfer.Receipt);
    }

    public static async Task<IResult> Create(HttpRequest http, TransferRequest request, LedgerDb db,
        CancellationToken cancellationToken)
    {
        var keys = http.Headers["Idempotency-Key"];
        var key = keys.ToString();
        if (keys.Count != 1 || key.Length is < 1 or > 128 || key.Any(c => c < '!' || c > '~' || c == ','))
            return Results.BadRequest(new { error = "Supply one Idempotency-Key of 1–128 visible ASCII characters, excluding commas." });

        var outcome = await PostAsync(key, "http", null, request, db, cancellationToken);
        if (outcome.Transfer is null)
            return Results.Json(new { error = outcome.Error }, statusCode: outcome.StatusCode);

        var transfer = outcome.Transfer;
        return Results.Created($"/transfers/{transfer.Id}", transfer.Receipt);
    }

    internal static async Task<PostingResult> PostAsync(string key, string scope, string? owner,
        TransferRequest request, LedgerDb db, CancellationToken cancellationToken)
    {
        await using var transaction = await db.Database.BeginTransactionAsync(cancellationToken);

        // Serialize attempts with the same key before checking for a committed transfer.
        var lockKey = scope + ":" + key;
        await db.Database.ExecuteSqlInterpolatedAsync(
            $"SELECT pg_advisory_xact_lock(hashtextextended({lockKey}, 0))", cancellationToken);

        var existing = await db.Transfers.AsNoTracking()
            .SingleOrDefaultAsync(t => t.Scope == scope && t.IdempotencyKey == key, cancellationToken);
        if (existing is not null)
        {
            if (existing.SourceAccountId != request.SourceAccountId
                || existing.DestinationAccountId != request.DestinationAccountId
                || existing.AmountMinor != request.AmountMinor)
                return new(null, 409, "Idempotency key was used with a different payload.");

            if (owner is not null && !await db.Accounts.AnyAsync(a => a.Id == existing.SourceAccountId
                    && a.OwnerSubject == owner, cancellationToken))
                return new(null, 404, "Account not found.");
            return new(existing);
        }

        if (request.AmountMinor <= 0 || request.SourceAccountId == request.DestinationAccountId)
            return new(null, 400, "Use a positive amount and two distinct accounts.");

        // PostgreSQL locks both rows in a consistent order, including for reverse transfers.
        var accounts = await db.Accounts.FromSqlInterpolated($"""
            SELECT * FROM "Accounts"
            WHERE "Id" IN ({request.SourceAccountId}, {request.DestinationAccountId})
            ORDER BY "Id" FOR UPDATE
            """).ToListAsync(cancellationToken);
        if (accounts.Count != 2)
            return new(null, 404, "Account not found.");

        var source = accounts.Single(a => a.Id == request.SourceAccountId);
        var destination = accounts.Single(a => a.Id == request.DestinationAccountId);
        if (owner is not null && source.OwnerSubject != owner)
            return new(null, 404, "Account not found.");
        if (source.Balance < request.AmountMinor)
            return new(null, 409, "Insufficient funds.");
        if (destination.Balance > long.MaxValue - request.AmountMinor)
            return new(null, 409, "Destination balance would exceed its limit.");

        // PostgreSQL timestamps have microsecond precision. Keep retries byte-for-byte stable.
        var now = DateTimeOffset.UtcNow;
        var completedAt = new DateTimeOffset(now.Ticks - now.Ticks % 10, TimeSpan.Zero);
        var transfer = new Transfer(Guid.NewGuid(), key, source.Id, destination.Id, request.AmountMinor, completedAt)
            { Scope = scope };
        source.Balance -= transfer.AmountMinor;
        destination.Balance += transfer.AmountMinor;
        db.Transfers.Add(transfer);
        db.Outbox.Add(OutboxMessage.FromTransfer(transfer));
        db.Entries.AddRange(new LedgerEntry(transfer.Id, source.Id, -transfer.AmountMinor),
            new LedgerEntry(transfer.Id, destination.Id, transfer.AmountMinor));
        await db.SaveChangesAsync(cancellationToken);
        await transaction.CommitAsync(cancellationToken);
        return new(transfer);
    }

    internal sealed record PostingResult(Transfer? Transfer, int StatusCode = 201, string? Error = null);
}
