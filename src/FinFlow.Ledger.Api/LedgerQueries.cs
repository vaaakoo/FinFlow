using System.Security.Claims;
using Dapper;
using Microsoft.EntityFrameworkCore;

namespace FinFlow.Ledger.Api;

public static class LedgerQueries
{
    public static Task<IResult> Statement(Guid accountId, int? size, Guid? beforeId,
        DateTimeOffset? beforeTime, LedgerDb db, CancellationToken token) =>
        ReadStatement(accountId, size, beforeId, beforeTime, null, db, token);

    public static Task<IResult> OwnStatement(Guid accountId, int? size, Guid? beforeId,
        DateTimeOffset? beforeTime, ClaimsPrincipal user, LedgerDb db, CancellationToken token) =>
        ReadStatement(accountId, size, beforeId, beforeTime, user.FindFirst("sub")!.Value, db, token);

    private static async Task<IResult> ReadStatement(Guid accountId, int? size, Guid? beforeId,
        DateTimeOffset? beforeTime, string? owner, LedgerDb db, CancellationToken token)
    {
        var limit = size ?? 25;
        if (limit is < 1 or > 100 || beforeId.HasValue != beforeTime.HasValue)
            return Results.Problem(statusCode: 400, detail: "Use a size of 1 to 100 and supply both cursor values together.");

        if (!await db.Accounts.AnyAsync(account => account.Id == accountId
            && (owner == null || account.OwnerSubject == owner), token))
            return Results.NotFound();

        var rows = (await db.Database.GetDbConnection().QueryAsync<StatementEntry>(new CommandDefinition("""
            SELECT t."Id" AS "TransferId", t."CompletedAt", e."DeltaMinor"
            FROM "Entries" e JOIN "Transfers" t ON t."Id" = e."TransferId"
            WHERE e."AccountId" = @accountId
              AND (@beforeId::uuid IS NULL OR (t."CompletedAt", t."Id") < (@beforeTime, @beforeId))
            ORDER BY t."CompletedAt" DESC, t."Id" DESC
            LIMIT @take
            """, new { accountId, beforeId, beforeTime = beforeTime?.UtcDateTime, take = limit + 1 },
            cancellationToken: token))).ToList();
        var items = rows.Take(limit).ToArray();
        return Results.Ok(new
        {
            currency = "GEL",
            items,
            next = rows.Count > limit ? new { beforeId = items[^1].TransferId, beforeTime = items[^1].CompletedAt } : null
        });
    }

    public static async Task<IResult> Reconcile(LedgerDb db, CancellationToken token)
    {
        var result = await db.Database.GetDbConnection().QuerySingleAsync<Reconciliation>(new CommandDefinition("""
            SELECT COUNT(*) AS "TransfersChecked",
                   COUNT(*) FILTER (WHERE posted.debits <> 1 OR posted.credits <> 1 OR posted.total <> 2)
                       AS "InvalidPostings"
            FROM "Transfers" t
            CROSS JOIN LATERAL (
                SELECT COUNT(*) FILTER (WHERE e."AccountId" = t."SourceAccountId" AND e."DeltaMinor" = -t."AmountMinor") AS debits,
                       COUNT(*) FILTER (WHERE e."AccountId" = t."DestinationAccountId" AND e."DeltaMinor" = t."AmountMinor") AS credits,
                       COUNT(*) AS total
                FROM "Entries" e WHERE e."TransferId" = t."Id"
            ) posted
            """, cancellationToken: token));
        return Results.Ok(result);
    }

    public sealed class StatementEntry
    {
        public Guid TransferId { get; init; }
        public DateTime CompletedAt { get; init; }
        public long DeltaMinor { get; init; }
    }

    public sealed record Reconciliation(long TransfersChecked, long InvalidPostings);
}

