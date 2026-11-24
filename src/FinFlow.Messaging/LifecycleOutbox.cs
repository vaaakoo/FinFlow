using System.Diagnostics;
using System.Text.Json;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using RabbitMQ.Client;

namespace FinFlow.Messaging;

public sealed record LifecycleEvent(Guid EventId, string Kind, string EntityId, DateTimeOffset OccurredAt,
    string? CorrelationId);

public sealed class LifecycleOutbox
{
    public Guid Id { get; init; }
    public required string Kind { get; init; }
    public required string EntityId { get; init; }
    public DateTimeOffset OccurredAt { get; init; }
    public string? CorrelationId { get; init; }
    public DateTimeOffset? PublishedAt { get; set; }

    public static LifecycleOutbox Create(string kind, string entityId) => new()
    {
        Id = Guid.NewGuid(),
        Kind = kind,
        EntityId = entityId,
        OccurredAt = DateTimeOffset.UtcNow,
        CorrelationId = Activity.Current?.TraceId.ToString()
    };
}

public sealed class LifecyclePublisher<TDb> : BackgroundService where TDb : DbContext
{
    private readonly IServiceScopeFactory scopes;
    private readonly ConnectionFactory broker;
    private readonly ILogger<LifecyclePublisher<TDb>> logger;

    public LifecyclePublisher(IServiceScopeFactory scopes, ConnectionFactory broker, ILogger<LifecyclePublisher<TDb>> logger)
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
                await PublishAsync(stoppingToken);
            }
            catch (Exception ex) when (!stoppingToken.IsCancellationRequested)
            {
                logger.LogWarning(ex, "Lifecycle publishing failed. Pending events will retry.");
            }
            await Task.Delay(TimeSpan.FromSeconds(1), stoppingToken);
        }
    }

    private async Task PublishAsync(CancellationToken token)
    {
        await using var scope = scopes.CreateAsyncScope();
        var db = scope.ServiceProvider.GetRequiredService<TDb>();
        var pending = await db.Set<LifecycleOutbox>().Where(row => row.PublishedAt == null)
            .OrderBy(row => row.OccurredAt).ThenBy(row => row.Id).Take(32).ToListAsync(token);
        if (pending.Count == 0)
            return;

        await using var connection = await broker.CreateConnectionAsync(token);
        await using var channel = await connection.CreateChannelAsync(new CreateChannelOptions(true, true), token);
        await RabbitTopology.DeclareLifecycleQueueAsync(channel, token);
        foreach (var row in pending)
        {
            using var timeout = CancellationTokenSource.CreateLinkedTokenSource(token);
            timeout.CancelAfter(TimeSpan.FromSeconds(8));
            var message = new LifecycleEvent(row.Id, row.Kind, row.EntityId, row.OccurredAt, row.CorrelationId);
            var properties = new BasicProperties
            {
                Persistent = true,
                Type = nameof(LifecycleEvent),
                MessageId = row.Id.ToString(),
                ContentType = "application/json"
            };
            await channel.BasicPublishAsync(RabbitTopology.Exchange, RabbitTopology.LifecycleRoutingKey,
                mandatory: true, properties, JsonSerializer.SerializeToUtf8Bytes(message), timeout.Token);
            row.PublishedAt = DateTimeOffset.UtcNow;
            await db.SaveChangesAsync(token);
        }
    }
}
