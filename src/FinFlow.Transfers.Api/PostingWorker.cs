using System.Net.Http.Json;
using System.Text.Json.Serialization;
using FinFlow.Ledger.Contracts;
using Grpc.Core;
using Microsoft.EntityFrameworkCore;

namespace FinFlow.Transfers.Api;

public sealed class PostingWorker : BackgroundService
{
    private readonly IServiceScopeFactory scopes;
    private readonly LedgerPosting.LedgerPostingClient ledger;
    private readonly IHttpClientFactory clients;
    private readonly PostingOptions options;
    private readonly ILogger<PostingWorker> logger;

    public PostingWorker(IServiceScopeFactory scopes,
        LedgerPosting.LedgerPostingClient ledger,
        IHttpClientFactory clients,
        PostingOptions options,
        ILogger<PostingWorker> logger)
    {
        this.scopes = scopes;
        this.ledger = ledger;
        this.clients = clients;
        this.options = options;
        this.logger = logger;
    }

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        while (!stoppingToken.IsCancellationRequested)
        {
            try
            {
                if (await DispatchNextOrderAsync(stoppingToken))
                    continue;
            }
            catch (Exception exception) when (!stoppingToken.IsCancellationRequested)
            {
                logger.LogWarning(exception, "Reading or updating the transfer queue failed.");
            }
            await Task.Delay(TimeSpan.FromMilliseconds(400), stoppingToken);
        }
    }

    private async Task<bool> DispatchNextOrderAsync(CancellationToken stoppingToken)
    {
        await using var scope = scopes.CreateAsyncScope();
        var db = scope.ServiceProvider.GetRequiredService<TransfersDb>();
        await using var transaction = await db.Database.BeginTransactionAsync(stoppingToken);
        // Hold the order lock through the bounded call so two workers cannot race to finalize it.
        var pendingOrders = await db.Orders.FromSqlInterpolated($"""
            SELECT * FROM "Orders"
            WHERE "Status" = 'Pending' AND "NextAttempt" <= {DateTimeOffset.UtcNow}
            ORDER BY "NextAttempt", "Id" LIMIT 1 FOR UPDATE SKIP LOCKED
            """).ToListAsync(stoppingToken);
        if (pendingOrders.Count == 0)
            return false;

        await PostOrderAsync(pendingOrders[0], stoppingToken);
        await db.SaveChangesAsync(stoppingToken);
        await transaction.CommitAsync(stoppingToken);
        return true;
    }

    private async Task PostOrderAsync(TransferOrder order, CancellationToken stoppingToken)
    {
        using var timeout = CancellationTokenSource.CreateLinkedTokenSource(stoppingToken);
        timeout.CancelAfter(TimeSpan.FromSeconds(8));
        try
        {
            var accessToken = await GetAccessTokenAsync(timeout.Token);
            var request = new PostTransfer
            {
                OperationId = order.Id.ToString(),
                OwnerSubject = order.Subject,
                SourceAccountId = order.SourceAccountId.ToString(),
                DestinationAccountId = order.DestinationAccountId.ToString(),
                AmountMinor = order.AmountMinor
            };
            var headers = new Metadata { { "authorization", "Bearer " + accessToken } };
            var outcome = await ledger.PostAsync(request, headers, cancellationToken: timeout.Token);
            if (Guid.TryParse(outcome.TransferId, out var postingId))
            {
                order.PostingId = postingId;
                order.Status = OrderStatus.Completed;
            }
            else if (!string.IsNullOrEmpty(outcome.Rejection))
            {
                order.Rejection = outcome.Rejection;
                order.Status = OrderStatus.Rejected;
            }
            else
            {
                throw new InvalidOperationException("Ledger returned an incomplete posting outcome.");
            }
        }
        catch (Exception exception) when (!stoppingToken.IsCancellationRequested)
        {
            order.Attempts = Math.Min(order.Attempts + 1, 8);
            order.NextAttempt = DateTimeOffset.UtcNow.AddSeconds(Math.Min(60, Math.Pow(2, order.Attempts)));
            logger.LogWarning("Ledger call for {OrderId} did not complete ({ExceptionType}). Retrying the operation.",
                order.Id, exception.GetType().Name);
        }
    }

    private async Task<string> GetAccessTokenAsync(CancellationToken token)
    {
        using var client = clients.CreateClient();
        using var form = new FormUrlEncodedContent(new Dictionary<string, string>
        {
            ["grant_type"] = "client_credentials",
            ["client_id"] = "finflow-transfers",
            ["client_secret"] = options.ClientSecret
        });
        using var response = await client.PostAsync(options.TokenEndpoint, form, token);
        response.EnsureSuccessStatusCode();
        return (await response.Content.ReadFromJsonAsync<TokenResponse>(token))?.AccessToken
            ?? throw new InvalidOperationException("Identity provider returned no access token.");
    }

    private sealed record TokenResponse([property: JsonPropertyName("access_token")] string AccessToken);
}

public sealed record PostingOptions(string TokenEndpoint, string ClientSecret);

