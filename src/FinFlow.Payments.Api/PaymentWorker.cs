using System.Text.Json;
using FinFlow.Ledger.Contracts;
using FinFlow.Observability;
using Grpc.Core;
using Microsoft.EntityFrameworkCore;
using Polly;
using Polly.CircuitBreaker;
using Polly.Retry;

namespace FinFlow.Payments.Api;

public sealed class PaymentWorker : BackgroundService
{
    private readonly IServiceScopeFactory scopes;
    private readonly LedgerPosting.LedgerPostingClient ledger;
    private readonly IHttpClientFactory clients;
    private readonly string tokenEndpoint;
    private readonly string clientSecret;
    private readonly ILogger<PaymentWorker> logger;
    private readonly ResiliencePipeline ledgerCalls;

    public PaymentWorker(IServiceScopeFactory scopes, LedgerPosting.LedgerPostingClient ledger,
        IHttpClientFactory clients, IConfiguration configuration, ILogger<PaymentWorker> logger)
    {
        this.scopes = scopes;
        this.ledger = ledger;
        this.clients = clients;
        this.logger = logger;
        tokenEndpoint = configuration["LedgerPosting:TokenEndpoint"]
            ?? throw new InvalidOperationException("Configure LedgerPosting:TokenEndpoint.");
        clientSecret = configuration["LedgerPosting:ClientSecret"]
            ?? throw new InvalidOperationException("Configure LedgerPosting:ClientSecret.");
        var transient = new PredicateBuilder()
            .Handle<RpcException>(exception => exception.StatusCode is StatusCode.Unavailable or StatusCode.DeadlineExceeded)
            .Handle<HttpRequestException>(exception => exception.StatusCode is null || (int)exception.StatusCode >= 500);
        ledgerCalls = new ResiliencePipelineBuilder()
            .AddRetry(new RetryStrategyOptions
            {
                ShouldHandle = transient,
                MaxRetryAttempts = 2,
                Delay = TimeSpan.FromMilliseconds(200),
                BackoffType = DelayBackoffType.Exponential,
                UseJitter = true
            })
            .AddCircuitBreaker(new CircuitBreakerStrategyOptions
            {
                ShouldHandle = transient,
                FailureRatio = 0.5,
                MinimumThroughput = 4,
                SamplingDuration = TimeSpan.FromSeconds(20),
                BreakDuration = TimeSpan.FromSeconds(10)
            })
            .Build();
    }

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        while (!stoppingToken.IsCancellationRequested)
        {
            try
            {
                if (await AdvanceNextAsync(stoppingToken))
                    continue;
            }
            catch (Exception exception) when (!stoppingToken.IsCancellationRequested)
            {
                logger.LogWarning(exception, "Payment queue unavailable.");
            }

            await Task.Delay(TimeSpan.FromMilliseconds(400), stoppingToken);
        }
    }

    private async Task<bool> AdvanceNextAsync(CancellationToken token)
    {
        await using var scope = scopes.CreateAsyncScope();
        var db = scope.ServiceProvider.GetRequiredService<PaymentsDb>();
        await using var transaction = await db.Database.BeginTransactionAsync(token);
        var now = DateTimeOffset.UtcNow;
        var due = await db.Payments.FromSqlInterpolated($"""
            SELECT * FROM "Payments" WHERE "NextAttempt" <= {now}
                AND "Status" IN ({(int)PaymentStatus.Pending}, {(int)PaymentStatus.Charged},
                    {(int)PaymentStatus.CompensationPending}, {(int)PaymentStatus.RefundPending})
            ORDER BY "NextAttempt", "Id" LIMIT 1 FOR UPDATE SKIP LOCKED
            """).ToListAsync(token);
        if (due.Count == 0)
            return false;

        var projection = due[0];
        var payment = await db.ReplayForWriteAsync(projection, token);
        if (payment.NextAttempt > now || payment.Status is not (PaymentStatus.Pending or PaymentStatus.Charged
            or PaymentStatus.CompensationPending or PaymentStatus.RefundPending))
            throw new InvalidOperationException("Payment scheduling projection does not match its event stream.");

        var expectedVersion = payment.Version;
        var previousStatus = payment.Status;
        using var timeout = CancellationTokenSource.CreateLinkedTokenSource(token);
        timeout.CancelAfter(TimeSpan.FromSeconds(8));
        try
        {
            await AdvanceAsync(db, payment, timeout.Token);
        }
        catch (Exception exception) when (!token.IsCancellationRequested)
        {
            payment.ScheduleRetry(db, DateTimeOffset.UtcNow);
            logger.LogWarning("Payment {PaymentId} in {Status} will retry after {FailureType}.",
                payment.Id, payment.Status, exception.GetType().Name);
        }

        projection.UpdateProjection(payment, expectedVersion);
        await db.SaveChangesAsync(token);
        await transaction.CommitAsync(token);
        if (previousStatus != PaymentStatus.Settled && payment.Status == PaymentStatus.Settled)
            FinFlowTelemetry.CompletedPayments.Add(1);
        return true;
    }

    private async Task AdvanceAsync(PaymentsDb db, Payment payment, CancellationToken token)
    {
        if (payment.Status == PaymentStatus.Charged)
        {
            var receipts = await SimulatedProvider.SettleAsync(payment, token);
            foreach (var receipt in receipts)
            {
                if (receipt.PaymentId != payment.Id || payment.Status != PaymentStatus.Charged)
                    continue;
                if (receipt.Approved)
                    payment.MoveTo(db, PaymentStatus.Settled);
                else
                    payment.MoveTo(db, PaymentStatus.CompensationPending, "Provider declined the payment.");
            }
            return;
        }

        if (payment.Status == PaymentStatus.Pending)
        {
            var charge = new ChargePayment
            {
                PaymentId = payment.Id.ToString(),
                OwnerSubject = payment.Subject,
                SourceAccountId = payment.AccountId.ToString(),
                AmountMinor = payment.AmountMinor
            };
            var reply = await CallLedgerAsync(
                (headers, ct) => ledger.ChargeAsync(charge, headers, cancellationToken: ct).ResponseAsync, token);
            if (Guid.TryParse(reply.TransferId, out var chargeId))
            {
                payment.ChargeId = chargeId;
                payment.MoveTo(db, PaymentStatus.Charged);
            }
            else
            {
                payment.MoveTo(db, PaymentStatus.Failed, ReadRejection(reply));
            }
            return;
        }

        var request = new RefundPayment { PaymentId = payment.Id.ToString() };
        var refund = await CallLedgerAsync(
            (headers, ct) => ledger.RefundAsync(request, headers, cancellationToken: ct).ResponseAsync, token);
        if (!Guid.TryParse(refund.TransferId, out var refundId))
        {
            if (payment.Status == PaymentStatus.CompensationPending)
                throw new InvalidOperationException(ReadRejection(refund));

            payment.MoveTo(db, PaymentStatus.RefundRejected, ReadRejection(refund));
            return;
        }

        payment.RefundId = refundId;
        if (payment.Status == PaymentStatus.CompensationPending)
            payment.MoveTo(db, PaymentStatus.Failed, "Provider declined the payment. The charge was reversed.");
        else
            payment.MoveTo(db, PaymentStatus.Refunded);
    }

    private ValueTask<PostingOutcome> CallLedgerAsync(
        Func<Metadata, CancellationToken, Task<PostingOutcome>> call, CancellationToken token) =>
        ledgerCalls.ExecuteAsync(async ct => await call(await GetLedgerHeadersAsync(ct), ct), token);

    private static string ReadRejection(PostingOutcome reply)
    {
        if (string.IsNullOrEmpty(reply.Rejection))
            throw new InvalidOperationException("Ledger returned no posting outcome.");
        return reply.Rejection;
    }

    private async Task<Metadata> GetLedgerHeadersAsync(CancellationToken token)
    {
        using var client = clients.CreateClient();
        using var form = new FormUrlEncodedContent(new Dictionary<string, string>
        {
            ["grant_type"] = "client_credentials",
            ["client_id"] = "finflow-payments",
            ["client_secret"] = clientSecret
        });
        using var response = await client.PostAsync(tokenEndpoint, form, token);
        response.EnsureSuccessStatusCode();
        using var payload = await JsonDocument.ParseAsync(await response.Content.ReadAsStreamAsync(token),
            cancellationToken: token);
        var accessToken = payload.RootElement.GetProperty("access_token").GetString();
        if (string.IsNullOrEmpty(accessToken))
            throw new InvalidOperationException("Identity provider returned no access token.");
        return new Metadata { { "authorization", "Bearer " + accessToken } };
    }
}
