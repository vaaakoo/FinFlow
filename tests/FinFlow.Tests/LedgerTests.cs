using System.Net;
using System.Net.Http.Json;
using System.Text.Json;
using FinFlow.Ledger.Api;
using Microsoft.AspNetCore.Mvc.Testing;
using Microsoft.AspNetCore.TestHost;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.Hosting;
using Npgsql;

namespace FinFlow.Tests;

public sealed class LedgerTests : IClassFixture<PostgresFixture>, IAsyncLifetime
{
    private readonly PostgresFixture postgres;
    private WebApplicationFactory<Program> app = null!;
    private HttpClient client = null!;
    private LedgerDb db = null!;

    public LedgerTests(PostgresFixture postgres) => this.postgres = postgres;

    public async Task InitializeAsync()
    {
        var connection = await postgres.CreateDatabaseAsync();
        app = new WebApplicationFactory<Program>().WithWebHostBuilder(builder =>
        {
            builder.ConfigureAppConfiguration((_, config) => config.AddInMemoryCollection(
                new Dictionary<string, string?> { ["ConnectionStrings:Ledger"] = connection }));
            builder.ConfigureTestServices(services => services.Remove(services.Single(descriptor =>
                descriptor.ServiceType == typeof(IHostedService) && descriptor.ImplementationType == typeof(OutboxPublisher))));
        });
        client = app.CreateClient();
        db = new LedgerDb(new DbContextOptionsBuilder<LedgerDb>().UseNpgsql(connection).Options);
    }

    public async Task DisposeAsync()
    {
        client.Dispose();
        await db.DisposeAsync();
        await app.DisposeAsync();
    }

    [Fact]
    public async Task Posted_entries_are_immutable_and_statement_pages_reconcile()
    {
        Assert.Equal(HttpStatusCode.Created, (await Send("statement-one", 120)).StatusCode);
        Assert.Equal(HttpStatusCode.Created, (await Send("statement-two", 240)).StatusCode);

        var rejectedChange = await Assert.ThrowsAsync<PostgresException>(() =>
            db.Database.ExecuteSqlRawAsync("""UPDATE "Entries" SET "DeltaMinor" = 0"""));
        Assert.Equal(PostgresErrorCodes.CheckViolation, rejectedChange.SqlState);
        var id = Guid.NewGuid();
        var rejectedPosting = await Assert.ThrowsAsync<PostgresException>(() =>
            db.Database.ExecuteSqlInterpolatedAsync($"""
                INSERT INTO "Transfers" ("Id", "IdempotencyKey", "SourceAccountId", "DestinationAccountId",
                    "AmountMinor", "CompletedAt")
                VALUES ({id}, 'missing-entries', {LedgerDb.Alice}, {LedgerDb.Bob}, 42, now())
                """));
        Assert.Equal(PostgresErrorCodes.CheckViolation, rejectedPosting.SqlState);

        var statement = $"/accounts/{LedgerDb.Alice}/statement?size=1";
        var page = await client.GetFromJsonAsync<JsonElement>(statement);
        Assert.Equal(-240, page.GetProperty("items")[0].GetProperty("deltaMinor").GetInt64());
        var cursor = page.GetProperty("next");
        var time = Uri.EscapeDataString(cursor.GetProperty("beforeTime").GetString()!);
        var nextPage = await client.GetFromJsonAsync<JsonElement>(
            $"{statement}&beforeId={cursor.GetProperty("beforeId").GetGuid()}&beforeTime={time}");
        Assert.Equal(-120, nextPage.GetProperty("items")[0].GetProperty("deltaMinor").GetInt64());
        Assert.Equal(JsonValueKind.Null, nextPage.GetProperty("next").ValueKind);
        var reconciliation = await client.GetFromJsonAsync<JsonElement>("/staff/ledger/reconciliation");
        Assert.Equal(2, reconciliation.GetProperty("transfersChecked").GetInt64());
        Assert.Equal(0, reconciliation.GetProperty("invalidPostings").GetInt64());
    }

    [Fact]
    public async Task Concurrent_requests_with_the_same_key_and_different_amounts_conflict()
    {
        var responses = await Task.WhenAll(Send("collision", 42), Send("collision", 43));
        var success = Assert.Single(responses, r => r.StatusCode == HttpStatusCode.Created);
        Assert.Single(responses, r => r.StatusCode == HttpStatusCode.Conflict);
        var receipt = Assert.IsType<TransferReceipt>(await success.Content.ReadFromJsonAsync<TransferReceipt>());
        await AssertLedger(receipt.AmountMinor);
    }

    [Fact]
    public async Task Successful_transfer_can_be_read_and_has_balanced_entries()
    {
        Assert.Equal(HttpStatusCode.OK, (await client.GetAsync("/accounts")).StatusCode);
        var response = await Send("success", 1234);
        Assert.Equal(HttpStatusCode.Created, response.StatusCode);
        var receipt = Assert.IsType<TransferReceipt>(await response.Content.ReadFromJsonAsync<TransferReceipt>());
        Assert.Equal(receipt, await client.GetFromJsonAsync<TransferReceipt>(response.Headers.Location));
        await AssertLedger(1234);
    }

    [Fact]
    public async Task Insufficient_funds_leave_no_entries()
    {
        Assert.Equal(HttpStatusCode.Conflict, (await Send("too-much", 100_001)).StatusCode);
        await AssertLedger(0);
    }

    [Fact]
    public async Task Concurrent_debits_cannot_overdraw()
    {
        var responses = await Task.WhenAll(Send("first", 75_000), Send("second", 75_000));
        Assert.Single(responses, r => r.StatusCode == HttpStatusCode.Created);
        Assert.Single(responses, r => r.StatusCode == HttpStatusCode.Conflict);
        await AssertLedger(75_000);
    }

    [Fact]
    public async Task Concurrent_retries_replay_original_result_and_reject_changed_payload()
    {
        var responses = await Task.WhenAll(Enumerable.Range(0, 8).Select(_ => Send("retry", 42)));
        Assert.All(responses, r => Assert.Equal(HttpStatusCode.Created, r.StatusCode));
        var bodies = await Task.WhenAll(responses.Select(r => r.Content.ReadAsStringAsync()));
        Assert.Single(bodies.Distinct());
        Assert.Equal(bodies[0], await (await Send("retry", 42)).Content.ReadAsStringAsync());
        Assert.Equal(HttpStatusCode.Conflict, (await Send("retry", 43)).StatusCode);
        await AssertLedger(42);
    }

    [Fact]
    public async Task Invalid_requests_do_not_change_the_ledger()
    {
        Assert.Equal(HttpStatusCode.BadRequest, (await Send("zero", 0)).StatusCode);
        Assert.Equal(HttpStatusCode.BadRequest, (await Send("same", 1, LedgerDb.Alice)).StatusCode);
        Assert.Equal(HttpStatusCode.NotFound, (await Send("missing", 1, Guid.NewGuid())).StatusCode);
        Assert.Equal(HttpStatusCode.BadRequest, (await Send(null, 1)).StatusCode);
        await AssertLedger(0);
    }

    private async Task<HttpResponseMessage> Send(string? key, long amount, Guid? destination = null)
    {
        using var request = new HttpRequestMessage(HttpMethod.Post, "/transfers")
        {
            Content = JsonContent.Create(new TransferRequest(LedgerDb.Alice, destination ?? LedgerDb.Bob, amount))
        };
        if (key is not null)
            request.Headers.Add("Idempotency-Key", key);
        return await client.SendAsync(request);
    }

    private async Task AssertLedger(long moved)
    {
        Assert.Equal(100_000 - moved, (await db.Accounts.FindAsync(LedgerDb.Alice))!.Balance);
        Assert.Equal(50_000 + moved, (await db.Accounts.FindAsync(LedgerDb.Bob))!.Balance);
        var entries = await db.Entries.OrderBy(e => e.DeltaMinor).ToListAsync();
        if (moved == 0)
        {
            Assert.Empty(entries);
            Assert.Empty(await db.Transfers.ToListAsync());
            Assert.Empty(await db.Outbox.ToListAsync());
            return;
        }

        var transfer = Assert.Single(await db.Transfers.ToListAsync());
        Assert.Null(Assert.Single(await db.Outbox.ToListAsync()).PublishedAt);
        Assert.Equal(new[]
        {
            new LedgerEntry(transfer.Id, LedgerDb.Alice, -moved),
            new LedgerEntry(transfer.Id, LedgerDb.Bob, moved)
        }, entries);
    }
}

