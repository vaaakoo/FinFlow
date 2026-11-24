using System.Text;
using RabbitMQ.Client;

namespace FinFlow.Messaging;

public static class RabbitTopology
{
    public const string Exchange = "finflow.events";
    public const string TransferCompletedRoutingKey = "ledger.transfer-completed.v1";
    public const string Queue = "finflow.transfer-completed.v1";
    public const string NotificationDeadLetterQueue = "finflow.notifications.dead-letter";
    public const string AuditQueue = "finflow.audit.transfer-completed.v1";
    public const string AuditDeadLetterQueue = "finflow.audit.dead-letter";
    public const string LifecycleAuditQueue = "finflow.audit.lifecycle.v1";
    public const string LifecycleRoutingKey = "workflow.lifecycle.v1";

    public static Dictionary<string, object?>? TraceHeaders(string? traceParent)
    {
        if (string.IsNullOrEmpty(traceParent))
            return null;
        return new Dictionary<string, object?> { ["traceparent"] = Encoding.ASCII.GetBytes(traceParent) };
    }

    public static string? ReadTraceParent(IReadOnlyBasicProperties properties)
    {
        if (properties.Headers?.TryGetValue("traceparent", out var value) == true && value is byte[] bytes)
            return Encoding.ASCII.GetString(bytes);
        return null;
    }

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

    public static async Task DeclareLifecycleQueueAsync(IChannel channel, CancellationToken token)
    {
        await channel.ExchangeDeclareAsync(Exchange, ExchangeType.Topic, durable: true, cancellationToken: token);
        await channel.QueueDeclareAsync(AuditDeadLetterQueue, durable: true, exclusive: false, autoDelete: false,
            cancellationToken: token);
        await channel.QueueDeclareAsync(LifecycleAuditQueue, durable: true, exclusive: false, autoDelete: false,
            arguments: new Dictionary<string, object?>
            {
                ["x-queue-type"] = "quorum",
                ["x-delivery-limit"] = 8,
                ["x-dead-letter-exchange"] = "",
                ["x-dead-letter-routing-key"] = AuditDeadLetterQueue,
                ["x-dead-letter-strategy"] = "at-least-once",
                ["x-overflow"] = "reject-publish"
            }, cancellationToken: token);
        await channel.QueueBindAsync(LifecycleAuditQueue, Exchange, LifecycleRoutingKey, cancellationToken: token);
    }
}

