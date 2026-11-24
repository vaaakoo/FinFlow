using System.Diagnostics;
using System.Text.Json;
using FinFlow.Messaging;
using FinFlow.Observability;
using Microsoft.AspNetCore.SignalR;
using RabbitMQ.Client;

namespace FinFlow.Notifications.Worker;

public sealed class NotificationConsumer : BackgroundService
{
    private readonly IServiceScopeFactory scopes;
    private readonly ConnectionFactory broker;
    private readonly ILogger<NotificationConsumer> logger;
    private readonly IHubContext<NotificationHub> hub;

    public NotificationConsumer(IServiceScopeFactory scopes,
        ConnectionFactory broker,
        IHubContext<NotificationHub> hub,
        ILogger<NotificationConsumer> logger)
    {
        this.scopes = scopes;
        this.broker = broker;
        this.logger = logger;
        this.hub = hub;
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
        var channelOptions = new CreateChannelOptions(
            publisherConfirmationsEnabled: true, publisherConfirmationTrackingEnabled: true);
        await using var channel = await connection.CreateChannelAsync(channelOptions, stoppingToken);
        await RabbitTopology.DeclareAsync(channel, stoppingToken);
        await channel.QueueDeclareAsync(RabbitTopology.NotificationDeadLetterQueue,
            durable: true, exclusive: false, autoDelete: false, cancellationToken: stoppingToken);

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
                await DeadLetterAsync(channel, delivery, stoppingToken);
                continue;
            }

            await using var scope = scopes.CreateAsyncScope();
            using var activity = FinFlowTelemetry.Start("notification.consume", ActivityKind.Consumer,
                RabbitTopology.ReadTraceParent(delivery.BasicProperties));
            var db = scope.ServiceProvider.GetRequiredService<NotificationsDb>();
            var notification = await db.StoreAsync(message, stoppingToken);
            await channel.BasicAckAsync(delivery.DeliveryTag, multiple: false, stoppingToken);
            if (notification?.RecipientSubject is string subject)
            {
                try
                {
                    await hub.Clients.User(subject).SendAsync("NotificationReceived",
                        NotificationEndpoints.Entry(notification), stoppingToken);
                }
                catch (Exception ex) when (!stoppingToken.IsCancellationRequested)
                {
                    logger.LogWarning("Live notification {EventId} could not be sent ({FailureType}).",
                        notification.EventId, ex.GetType().Name);
                }
            }
        }
    }

    private async Task DeadLetterAsync(IChannel channel, BasicGetResult delivery, CancellationToken stoppingToken)
    {
        using var timeout = CancellationTokenSource.CreateLinkedTokenSource(stoppingToken);
        timeout.CancelAfter(TimeSpan.FromSeconds(10));
        var properties = new BasicProperties
        {
            Persistent = true,
            MessageId = delivery.BasicProperties.MessageId,
            Type = delivery.BasicProperties.Type,
            ContentType = delivery.BasicProperties.ContentType
        };

        // Confirm the copy before acknowledging the original. Existing queue declarations remain valid.
        await channel.BasicPublishAsync("", RabbitTopology.NotificationDeadLetterQueue, mandatory: true,
            properties, delivery.Body, timeout.Token);
        await channel.BasicAckAsync(delivery.DeliveryTag, multiple: false, stoppingToken);
        logger.LogWarning("Moved invalid transfer event {MessageId} to the dead-letter queue.", properties.MessageId);
    }
}

