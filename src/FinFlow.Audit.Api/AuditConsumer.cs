using System.Diagnostics;
using System.Text.Json;
using FinFlow.Messaging;
using FinFlow.Observability;
using Microsoft.EntityFrameworkCore;
using RabbitMQ.Client;

namespace FinFlow.Audit.Api;

public sealed class AuditConsumer : BackgroundService
{
    private readonly IServiceScopeFactory scopes;
    private readonly ConnectionFactory broker;
    private readonly ILogger<AuditConsumer> logger;

    public AuditConsumer(IServiceScopeFactory scopes, ConnectionFactory broker, ILogger<AuditConsumer> logger)
    {
        this.scopes = scopes;
        this.broker = broker;
        this.logger = logger;
    }

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        while (!stoppingToken.IsCancellationRequested)
        {
            try
            {
                await ConsumeAsync(stoppingToken);
            }
            catch (OperationCanceledException) when (stoppingToken.IsCancellationRequested)
            {
                return;
            }
            catch (Exception exception)
            {
                logger.LogWarning(exception, "Audit consumption failed. Unacknowledged events will be retried.");
                await Task.Delay(TimeSpan.FromSeconds(1), stoppingToken);
            }
        }
    }

    private async Task ConsumeAsync(CancellationToken token)
    {
        await using var connection = await broker.CreateConnectionAsync(token);
        var channelOptions = new CreateChannelOptions(
            publisherConfirmationsEnabled: true, publisherConfirmationTrackingEnabled: true);
        await using var channel = await connection.CreateChannelAsync(channelOptions, token);
        await RabbitTopology.DeclareAuditQueueAsync(channel, token);
        await RabbitTopology.DeclareLifecycleQueueAsync(channel, token);

        while (!token.IsCancellationRequested)
        {
            var transfer = await channel.BasicGetAsync(RabbitTopology.AuditQueue, autoAck: false, token);
            var lifecycle = await channel.BasicGetAsync(RabbitTopology.LifecycleAuditQueue, autoAck: false, token);
            if (transfer is not null)
                await ProcessTransferAsync(channel, transfer, token);
            if (lifecycle is not null)
                await ProcessLifecycleAsync(channel, lifecycle, token);
            if (transfer is null && lifecycle is null)
                await Task.Delay(TimeSpan.FromMilliseconds(250), token);
        }
    }

    private async Task ProcessTransferAsync(IChannel channel, BasicGetResult delivery, CancellationToken token)
    {
        using var activity = FinFlowTelemetry.Start("audit.consume", ActivityKind.Consumer,
            RabbitTopology.ReadTraceParent(delivery.BasicProperties));
        var message = ReadEvent(delivery);
        if (message is null)
        {
            await DeadLetterAsync(channel, delivery, token);
            return;
        }

        await using var scope = scopes.CreateAsyncScope();
        var db = scope.ServiceProvider.GetRequiredService<AuditDb>();
        await db.Database.ExecuteSqlInterpolatedAsync($"""
            INSERT INTO "TransferEvents" ("EventId", "TransferId", "CompletedAt", "CorrelationId", "CausationId")
            VALUES ({message.EventId}, {message.TransferId}, {message.CompletedAt.ToUniversalTime()},
                {message.CorrelationId}, {message.CausationId})
            ON CONFLICT ("EventId") DO NOTHING
            """, token);
        await channel.BasicAckAsync(delivery.DeliveryTag, multiple: false, token);
    }

    private async Task ProcessLifecycleAsync(IChannel channel, BasicGetResult delivery, CancellationToken token)
    {
        LifecycleEvent? message;
        try
        {
            message = JsonSerializer.Deserialize<LifecycleEvent>(delivery.Body.Span);
        }
        catch (JsonException)
        {
            message = null;
        }

        if (message is null || message.EventId == Guid.Empty || message.OccurredAt == default
            || string.IsNullOrEmpty(message.Kind) || message.Kind.Length > 64
            || string.IsNullOrEmpty(message.EntityId) || message.EntityId.Length > 128
            || message.CorrelationId?.Length > 64 || delivery.BasicProperties.Type != nameof(LifecycleEvent)
            || delivery.BasicProperties.MessageId != message.EventId.ToString())
        {
            await DeadLetterAsync(channel, delivery, token);
            return;
        }

        await using var scope = scopes.CreateAsyncScope();
        var db = scope.ServiceProvider.GetRequiredService<AuditDb>();
        await db.Database.ExecuteSqlInterpolatedAsync($"""
            INSERT INTO "WorkflowEvents" ("EventId", "Kind", "EntityId", "OccurredAt", "CorrelationId")
            VALUES ({message.EventId}, {message.Kind}, {message.EntityId}, {message.OccurredAt.ToUniversalTime()},
                {message.CorrelationId})
            ON CONFLICT ("EventId") DO NOTHING
            """, token);
        await channel.BasicAckAsync(delivery.DeliveryTag, multiple: false, token);
    }

    private async Task DeadLetterAsync(IChannel channel, BasicGetResult delivery, CancellationToken token)
    {
        using var timeout = CancellationTokenSource.CreateLinkedTokenSource(token);
        timeout.CancelAfter(TimeSpan.FromSeconds(10));
        var properties = new BasicProperties
        {
            Persistent = true,
            MessageId = delivery.BasicProperties.MessageId,
            Type = delivery.BasicProperties.Type,
            ContentType = delivery.BasicProperties.ContentType
        };

        // Confirm the copy before acknowledging the original audit delivery.
        await channel.BasicPublishAsync("", RabbitTopology.AuditDeadLetterQueue, mandatory: true,
            properties, delivery.Body, timeout.Token);
        await channel.BasicAckAsync(delivery.DeliveryTag, multiple: false, token);
        logger.LogWarning("Moved malformed transfer audit event to the dead-letter queue.");
    }

    private static TransferCompleted? ReadEvent(BasicGetResult delivery)
    {
        if (delivery.BasicProperties.Type != nameof(TransferCompleted))
            return null;

        try
        {
            var message = JsonSerializer.Deserialize<TransferCompleted>(delivery.Body.Span);
            if (message is null || message.EventId == Guid.Empty || message.TransferId == Guid.Empty
                || message.CompletedAt == default || delivery.BasicProperties.MessageId != message.EventId.ToString()
                || message.CorrelationId?.Length > 64 || message.CausationId?.Length > 64)
                return null;

            return message;
        }
        catch (JsonException)
        {
            return null;
        }
    }
}

