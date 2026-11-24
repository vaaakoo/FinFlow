using System.Net;
using System.Net.Http.Json;
using System.Text.Json;
using FinFlow.Ledger.Api;
using FinFlow.Messaging;
using FinFlow.Notifications.Worker;
using Microsoft.AspNetCore.Mvc.Testing;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using RabbitMQ.Client;
using Testcontainers.RabbitMq;

namespace FinFlow.Tests;

public sealed class DeliveryTests(PostgresFixture postgres) : IClassFixture<PostgresFixture>
{
    [Fact]
    public async Task Broker_outage_preserves_transfer_and_recovery_handles_a_real_redelivery()
    {
        using var deadline = new CancellationTokenSource(TimeSpan.FromMinutes(3));
        var token = deadline.Token;
        await using var rabbit = new RabbitMqBuilder("rabbitmq:4-management-alpine").Build();
        await rabbit.StartAsync(token);
        var uri = rabbit.GetConnectionString();
        // Keep Docker's dynamically mapped host port fixed across the outage.
        var stopped = await rabbit.ExecAsync(["rabbitmqctl", "stop_app"], token);
        Assert.True(stopped.ExitCode == 0, stopped.Stderr);

        var ledgerConnection = await postgres.CreateDatabaseAsync();
        var notificationsConnection = await postgres.CreateDatabaseAsync();
        await using var app = new WebApplicationFactory<Program>().WithWebHostBuilder(builder =>
            builder.ConfigureAppConfiguration((_, config) => config.AddInMemoryCollection(
                new Dictionary<string, string?>
                {
                    ["ConnectionStrings:Ledger"] = ledgerConnection,
                    ["RabbitMq:Uri"] = uri
                })));
        using var client = app.CreateClient();
        await using var ledger = new LedgerDb(new DbContextOptionsBuilder<LedgerDb>().UseNpgsql(ledgerConnection).Options);
        using var worker = NotificationsApplication.Build(
            [$"--ConnectionStrings:Notifications={notificationsConnection}", $"--RabbitMq:Uri={uri}"]);
        await using var workerScope = worker.Services.CreateAsyncScope();
        var notifications = workerScope.ServiceProvider.GetRequiredService<NotificationsDb>();
        await notifications.Database.MigrateAsync(token);

        var first = await Send(client, "outage", 321, token);
        var pending = await ledger.Outbox.AsNoTracking().SingleAsync(token);
        Assert.Equal(first.Id, JsonSerializer.Deserialize<TransferCompleted>(pending.Payload)!.TransferId);
        Assert.Null(pending.PublishedAt);
        Assert.Equal(99_679, (await ledger.Accounts.FindAsync([LedgerDb.Alice], token))!.Balance);
        Assert.Equal(50_321, (await ledger.Accounts.FindAsync([LedgerDb.Bob], token))!.Balance);
        Assert.Equal(2, await ledger.Entries.CountAsync(e => e.TransferId == first.Id, token));
        Assert.Empty(await notifications.Notifications.ToListAsync(token));

        var started = await rabbit.ExecAsync(["rabbitmqctl", "start_app"], token);
        Assert.True(started.ExitCode == 0, started.Stderr);
        await EventuallyAsync(() => ledger.Outbox.AnyAsync(e => e.Id == pending.Id && e.PublishedAt != null, token), token);
        var broker = RabbitTopology.CreateFactory(uri);
        await using var connection = await broker.CreateConnectionAsync(token);

        // Simulate a consumer losing its channel after its database commit and before its ack.
        await using (var channel = await connection.CreateChannelAsync(cancellationToken: token))
        {
            var delivery = Assert.IsType<BasicGetResult>(
                await channel.BasicGetAsync(RabbitTopology.Queue, autoAck: false, token));
            Assert.False(delivery.Redelivered);
            Assert.Equal(RabbitTopology.Exchange, delivery.Exchange);
            Assert.Equal(RabbitTopology.TransferCompletedRoutingKey, delivery.RoutingKey);
            var message = Assert.IsType<TransferCompleted>(
                JsonSerializer.Deserialize<TransferCompleted>(delivery.Body.Span));
            Assert.Equal(pending.Id, message.EventId);
            Assert.False(string.IsNullOrWhiteSpace(message.CorrelationId));
            Assert.Equal(first.Id.ToString(), message.CausationId);
            await notifications.StoreAsync(message, token);
            await channel.CloseAsync(token);
        }

        await using (var channel = await connection.CreateChannelAsync(cancellationToken: token))
        {
            var replay = Assert.IsType<BasicGetResult>(
                await channel.BasicGetAsync(RabbitTopology.Queue, autoAck: false, token));
            Assert.True(replay.Redelivered);
            Assert.Equal(pending.Id.ToString(), replay.BasicProperties.MessageId);
            await channel.BasicNackAsync(replay.DeliveryTag, multiple: false, requeue: true, token);
        }

        // With one consumer, handling the next transfer proves it advanced past the replay.
        await worker.StartAsync(token);
        try
        {
            await using var channel = await connection.CreateChannelAsync(cancellationToken: token);
            await channel.BasicPublishAsync(RabbitTopology.Exchange, RabbitTopology.TransferCompletedRoutingKey,
                mandatory: true, new BasicProperties { MessageId = "invalid-test-event" },
                "invalid json"u8.ToArray(), token);
            var next = await Send(client, "following", 100, token);
            await EventuallyAsync(() => notifications.Notifications.AnyAsync(n => n.TransferId == next.Id, token), token);
            await EventuallyAsync(async () => await channel.MessageCountAsync(
                RabbitTopology.NotificationDeadLetterQueue, token) > 0, token);
            var deadLetter = await channel.BasicGetAsync(RabbitTopology.NotificationDeadLetterQueue, autoAck: true, token);
            Assert.NotNull(deadLetter);
            Assert.Equal("invalid-test-event", deadLetter.BasicProperties.MessageId);
            Assert.True(deadLetter.Body.Span.SequenceEqual("invalid json"u8));
            var records = await notifications.Notifications.AsNoTracking().ToListAsync(token);
            Assert.Equal(2, records.Count);
            var record = Assert.Single(records, n => n.TransferId == first.Id);
            Assert.Equal(pending.Id, record.EventId);
            Assert.Equal(LedgerDb.Bob, record.RecipientAccountId);
            Assert.Equal(321, record.AmountMinor);
            Assert.Equal("GEL", record.Currency);
            Assert.Equal(2, await notifications.ProcessedEvents.CountAsync(token));
        }
        finally
        {
            await worker.StopAsync(CancellationToken.None);
        }
    }

    private static async Task<TransferReceipt> Send(HttpClient client, string key, long amount, CancellationToken token)
    {
        using var request = new HttpRequestMessage(HttpMethod.Post, "/transfers")
        {
            Content = JsonContent.Create(new TransferRequest(LedgerDb.Alice, LedgerDb.Bob, amount))
        };
        request.Headers.Add("Idempotency-Key", key);
        using var response = await client.SendAsync(request, token);
        Assert.Equal(HttpStatusCode.Created, response.StatusCode);
        return Assert.IsType<TransferReceipt>(await response.Content.ReadFromJsonAsync<TransferReceipt>(token));
    }

    private static async Task EventuallyAsync(Func<Task<bool>> condition, CancellationToken token)
    {
        using var timeout = CancellationTokenSource.CreateLinkedTokenSource(token);
        timeout.CancelAfter(TimeSpan.FromSeconds(30));
        while (!await condition())
            await Task.Delay(200, timeout.Token);
    }
}

