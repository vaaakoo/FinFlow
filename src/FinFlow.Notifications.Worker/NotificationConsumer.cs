using System.Text.Json;
using FinFlow.Messaging;
using RabbitMQ.Client;

namespace FinFlow.Notifications.Worker;

public sealed class NotificationConsumer(IServiceScopeFactory scopes, ConnectionFactory broker,
    ILogger<NotificationConsumer> logger) : BackgroundService
{
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
            catch (Exception ex)
            {
                logger.LogWarning(ex, "Notification consumption failed. Unacknowledged deliveries will be retried.");
                await Task.Delay(TimeSpan.FromSeconds(1), stoppingToken);
            }
        }
    }

    private async Task ConsumeAsync(CancellationToken stoppingToken)
    {
        await using var connection = await broker.CreateConnectionAsync(stoppingToken);
        await using var channel = await connection.CreateChannelAsync(cancellationToken: stoppingToken);
        await RabbitTopology.DeclareAsync(channel, stoppingToken);

        while (!stoppingToken.IsCancellationRequested)
        {
            var delivery = await channel.BasicGetAsync(RabbitTopology.Queue, autoAck: false, stoppingToken);
            if (delivery is null)
            {
                await Task.Delay(TimeSpan.FromMilliseconds(250), stoppingToken);
                continue;
            }

            TransferCompleted? message;
            try
            {
                message = JsonSerializer.Deserialize<TransferCompleted>(delivery.Body.Span);
            }
            catch (JsonException)
            {
                message = null;
            }

            if (message is null || message.EventId == Guid.Empty || message.TransferId == Guid.Empty
                || message.DestinationAccountId == Guid.Empty || message.AmountMinor <= 0 || message.Currency != "GEL"
                || delivery.BasicProperties.Type != nameof(TransferCompleted)
                || delivery.BasicProperties.MessageId != message.EventId.ToString())
            {
                logger.LogWarning("Rejecting invalid transfer event {MessageId}.", delivery.BasicProperties.MessageId);
                await channel.BasicRejectAsync(delivery.DeliveryTag, requeue: false, stoppingToken);
                continue;
            }

            await using var scope = scopes.CreateAsyncScope();
            var db = scope.ServiceProvider.GetRequiredService<NotificationsDb>();
            await db.StoreAsync(message, stoppingToken);
            await channel.BasicAckAsync(delivery.DeliveryTag, multiple: false, stoppingToken);
        }
    }
}

