using System.Security.Claims;
using FinFlow.Messaging;
using Microsoft.EntityFrameworkCore;

namespace FinFlow.Payments.Api;

public static class PaymentEndpoints
{
    public static async Task<IResult> ListForStaff(string? status, int? page, PaymentsDb db, CancellationToken token)
    {
        var pageNumber = page ?? 1;
        if (pageNumber is < 1 or > 1000)
            return Results.Problem(statusCode: 400, detail: "Page must be between 1 and 1000.");

        var query = db.Payments.AsNoTracking();
        if (!string.IsNullOrEmpty(status))
        {
            if (!Enum.TryParse<PaymentStatus>(status, ignoreCase: true, out var parsed) || !Enum.IsDefined(parsed))
                return Results.Problem(statusCode: 400, detail: "Unknown payment status.");
            query = query.Where(payment => payment.Status == parsed);
        }

        var payments = await query.OrderByDescending(payment => payment.CreatedAt).ThenByDescending(payment => payment.Id)
            .Skip((pageNumber - 1) * 25).Take(25).ToListAsync(token);
        return Results.Ok(payments.Select(payment => payment.ToView()));
    }

    public static async Task<IResult> Submit(HttpRequest request, ClaimsPrincipal user, PaymentInput input,
        PaymentsDb db, CancellationToken token)
    {
        var keys = request.Headers["Idempotency-Key"];
        var key = keys.ToString();
        if (keys.Count != 1 || key.Length is < 1 or > 128 || key.Any(c => c < '!' || c > '~' || c == ','))
            return Results.Problem(statusCode: 400, detail: "Supply one valid Idempotency-Key.");
        if (input.AccountId == Guid.Empty || input.AmountMinor <= 0 || !SimulatedProvider.Scenarios.Contains(input.Scenario))
            return Results.Problem(statusCode: 400, detail: "Supply an account, a positive amount, and a supported scenario.");

        var subject = user.FindFirst("sub")!.Value;
        var id = Guid.NewGuid();
        var now = DateTimeOffset.UtcNow;
        await using var transaction = await db.Database.BeginTransactionAsync(token);
        var inserted = await db.Database.ExecuteSqlInterpolatedAsync($"""
            INSERT INTO "Payments" ("Id", "Subject", "Key", "AccountId", "AmountMinor", "Scenario",
                "Status", "CreatedAt", "NextAttempt", "Version", "FailedAttempts")
            VALUES ({id}, {subject}, {key}, {input.AccountId}, {input.AmountMinor}, {input.Scenario},
                {(int)PaymentStatus.Pending}, {now}, {now}, 1, 0)
            ON CONFLICT ("Subject", "Key") DO NOTHING
            """, token);
        if (inserted == 1)
        {
            db.Events.Add(new PaymentEvent(id, 1, PaymentStatus.Pending, now, null, null, null)
            {
                Subject = subject, RequestKey = key, AccountId = input.AccountId,
                AmountMinor = input.AmountMinor, Scenario = input.Scenario, NextAttempt = now
            });
            db.Add(LifecycleOutbox.Create("payment.requested", id.ToString()));
            await db.SaveChangesAsync(token);
        }

        var paymentId = await db.Payments.Where(p => p.Subject == subject && p.Key == key)
            .Select(p => p.Id).SingleAsync(token);
        var payment = await db.ReplayAsync(paymentId, token)
            ?? throw new InvalidOperationException("Payment is missing its event stream.");
        if (payment.Subject != subject || payment.Key != key)
            throw new InvalidOperationException("Payment lookup does not match its event stream.");
        if (payment.AccountId != input.AccountId || payment.AmountMinor != input.AmountMinor || payment.Scenario != input.Scenario)
            return Results.Problem(statusCode: 409, detail: "Idempotency key was used with a different payment.");

        await transaction.CommitAsync(token);
        return Accepted(payment);
    }

    public static async Task<IResult> Get(Guid id, ClaimsPrincipal user, PaymentsDb db, CancellationToken token)
    {
        var subject = user.FindFirst("sub")!.Value;
        var payment = await db.ReplayAsync(id, token);
        if (payment is null || payment.Subject != subject)
            return Results.NotFound();
        return Results.Ok(payment.ToView());
    }

    public static async Task<IResult> List(int? page, ClaimsPrincipal user, PaymentsDb db, CancellationToken token)
    {
        var pageNumber = page ?? 1;
        if (pageNumber is < 1 or > 1000)
            return Results.Problem(statusCode: 400, detail: "Page must be between 1 and 1000.");

        var subject = user.FindFirst("sub")!.Value;
        var payments = await db.Payments.AsNoTracking().Where(p => p.Subject == subject)
            .OrderByDescending(p => p.CreatedAt).ThenByDescending(p => p.Id)
            .Skip((pageNumber - 1) * 25).Take(25).ToListAsync(token);
        return Results.Ok(payments.Select(p => p.ToView()));
    }

    public static async Task<IResult> Refund(Guid id, ClaimsPrincipal user, PaymentsDb db, CancellationToken token)
    {
        var subject = user.FindFirst("sub")!.Value;
        await using var transaction = await db.Database.BeginTransactionAsync(token);
        var rows = await db.Payments.FromSqlInterpolated($"""
            SELECT * FROM "Payments" WHERE "Id" = {id} AND "Subject" = {subject} FOR UPDATE
            """).ToListAsync(token);
        var projection = rows.SingleOrDefault();
        if (projection is null)
            return Results.NotFound();
        var payment = await db.ReplayForWriteAsync(projection, token);
        if (payment.Subject != subject)
            return Results.NotFound();
        if (payment.Status is PaymentStatus.RefundPending or PaymentStatus.Refunded)
            return Accepted(payment);
        if (payment.Status != PaymentStatus.Settled && payment.Status != PaymentStatus.RefundRejected)
            return Results.Problem(statusCode: 409, detail: "Only settled payments can be refunded.");

        var expectedVersion = payment.Version;
        payment.MoveTo(db, PaymentStatus.RefundPending);
        projection.UpdateProjection(payment, expectedVersion);
        await db.SaveChangesAsync(token);
        await transaction.CommitAsync(token);
        return Accepted(payment);
    }

    private static IResult Accepted(Payment payment) =>
        Results.Accepted($"/customers/me/payments/{payment.Id}", payment.ToView());
}

public sealed record PaymentInput(Guid AccountId, long AmountMinor, string Scenario);
