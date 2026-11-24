using System.Diagnostics;
using System.Text;
using System.Text.Json;
using FinFlow.Messaging;
using FinFlow.Observability;
using Microsoft.EntityFrameworkCore;
using RabbitMQ.Client;

namespace FinFlow.Ledger.Api;

public sealed class OutboxPublisher : BackgroundService
{
    private readonly IServiceScopeFactory scopes;
    private readonly ConnectionFactory broker;
    private readonly ILogger<OutboxPublisher> logger;
    private readonly bool auditEnabled;

    public OutboxPublisher(IServiceScopeFactory scopes,
        ConnectionFactory broker,
        IConfiguration configuration,
        ILogger<OutboxPublisher> logger)
    {
        this.scopes = scopes;
        this.broker = broker;
        this.logger = logger;
        auditEnabled = configuration.GetValue<bool>("RabbitMq:AuditEnabled");
    }

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        while (!stoppingToken.IsCancellationRequested)
        {
            try
            {
                await PublishBatchAsync(stoppingToken);
            }
            catch (OperationCanceledException) when (stoppingToken.IsCancellationRequested)
            {
                return;
            }
            catch (Exception ex)
            {
                logger.LogWarning(ex, "Outbox publishing failed. Pending events will be retried.");
            }

            await Task.Delay(TimeSpan.FromSeconds(1), stoppingToken);
        }
    }

    private async Task PublishBatchAsync(CancellationToken stoppingToken)
    {
        await using var scope = scopes.CreateAsyncScope();
        var db = scope.ServiceProvider.GetRequiredService<LedgerDb>();
        var pending = await db.Outbox.Where(e => e.PublishedAt == null)
            .OrderBy(e => e.CreatedAt).ThenBy(e => e.Id).Take(32).ToListAsync(stoppingToken);
        if (pending.Count == 0)
            return;

        await using var connection = await broker.CreateConnectionAsync(stoppingToken);
        await using var channel = await connection.CreateChannelAsync(new CreateChannelOptions(
            publisherConfirmationsEnabled: true, publisherConfirmationTrackingEnabled: true), stoppingToken);
        await RabbitTopology.DeclareAsync(channel, stoppingToken);
        if (auditEnabled)
            await RabbitTopology.DeclareAuditQueueAsync(channel, stoppingToken);

        foreach (var message in pending)
        {
            using var activity = FinFlowTelemetry.Start("ledger.publish", ActivityKind.Producer, message.TraceParent);
            using var timeout = CancellationTokenSource.CreateLinkedTokenSource(stoppingToken);
            timeout.CancelAfter(TimeSpan.FromSeconds(10));
            await channel.BasicPublishAsync(RabbitTopology.Exchange, RabbitTopology.TransferCompletedRoutingKey,
                mandatory: true,
                basicProperties: new BasicProperties
                {
                    Persistent = true,
                    ContentType = "application/json",
                    Type = nameof(TransferCompleted),
                    MessageId = message.Id.ToString(),
                    Headers = RabbitTopology.TraceHeaders(activity?.Id ?? message.TraceParent)
                },
                body: Encoding.UTF8.GetBytes(message.Payload), cancellationToken: timeout.Token);

            message.PublishedAt = DateTimeOffset.UtcNow;
            await db.SaveChangesAsync(stoppingToken);
            FinFlowTelemetry.PublishedEvents.Add(1);
        }
    }
}

public sealed class OutboxMessage
{
    public Guid Id { get; init; }
    public DateTimeOffset CreatedAt { get; init; }
    public required string Payload { get; init; }
    public DateTimeOffset? PublishedAt { get; set; }
    public string? TraceParent { get; init; }

    public static OutboxMessage FromTransfer(Transfer transfer, string? recipientSubject)
    {
        var transferId = transfer.Id.ToString();
        var correlationId = Activity.Current?.TraceId.ToString() ?? transferId;
        var causationId = transfer.Scope == "http" ? transferId : transfer.IdempotencyKey;
        var message = new TransferCompleted(Guid.NewGuid(), transfer.Id, transfer.SourceAccountId,
            transfer.DestinationAccountId, transfer.AmountMinor, "GEL", transfer.CompletedAt,
            correlationId, causationId, recipientSubject);
        return new OutboxMessage
        {
            Id = message.EventId,
            CreatedAt = message.CompletedAt,
            TraceParent = Activity.Current?.Id,
            Payload = JsonSerializer.Serialize(message)
        };
    }
}

