using System.Text.Json;
using FinFlow.Messaging;
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
        await using var channel = await connection.CreateChannelAsync(cancellationToken: token);
        await RabbitTopology.DeclareAuditQueueAsync(channel, token);

        while (!token.IsCancellationRequested)
        {
            var delivery = await channel.BasicGetAsync(RabbitTopology.AuditQueue, autoAck: false, token);
            if (delivery is null)
            {
                await Task.Delay(TimeSpan.FromMilliseconds(250), token);
                continue;
            }

            var message = ReadEvent(delivery);
            if (message is null)
            {
                logger.LogWarning("Rejected malformed transfer audit event.");
                await channel.BasicRejectAsync(delivery.DeliveryTag, requeue: false, token);
                continue;
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

