using RabbitMQ.Client;

namespace FinFlow.Messaging;

public static class RabbitTopology
{
    public const string Exchange = "finflow.events";
    public const string TransferCompletedRoutingKey = "ledger.transfer-completed.v1";
    public const string Queue = "finflow.transfer-completed.v1";
    public const string NotificationDeadLetterQueue = "finflow.notifications.dead-letter";
    public const string AuditQueue = "finflow.audit.transfer-completed.v1";

    public static ConnectionFactory CreateFactory(string uri) => new()
    {
        Uri = new Uri(uri),
        AutomaticRecoveryEnabled = false,
        RequestedConnectionTimeout = TimeSpan.FromSeconds(5),
        ContinuationTimeout = TimeSpan.FromSeconds(5)
    };

    public static async Task DeclareAsync(IChannel channel, CancellationToken cancellationToken)
    {
        await channel.ExchangeDeclareAsync(Exchange, ExchangeType.Topic, durable: true,
            cancellationToken: cancellationToken);
        await channel.QueueDeclareAsync(Queue, durable: true, exclusive: false, autoDelete: false,
            cancellationToken: cancellationToken);
        await channel.QueueBindAsync(Queue, Exchange, TransferCompletedRoutingKey,
            cancellationToken: cancellationToken);
    }

    public static async Task DeclareAuditQueueAsync(IChannel channel, CancellationToken cancellationToken)
    {
        await channel.ExchangeDeclareAsync(Exchange, ExchangeType.Topic, durable: true,
            cancellationToken: cancellationToken);
        await channel.QueueDeclareAsync(AuditQueue, durable: true, exclusive: false, autoDelete: false,
            cancellationToken: cancellationToken);
        await channel.QueueBindAsync(AuditQueue, Exchange, TransferCompletedRoutingKey,
            cancellationToken: cancellationToken);
    }
}

