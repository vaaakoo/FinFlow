using FinFlow.Messaging;
using Microsoft.EntityFrameworkCore;

namespace FinFlow.Notifications.Worker;

public sealed class NotificationsDb(DbContextOptions<NotificationsDb> options) : DbContext(options)
{
    public DbSet<ProcessedEvent> ProcessedEvents => Set<ProcessedEvent>();
    public DbSet<Notification> Notifications => Set<Notification>();

    protected override void OnModelCreating(ModelBuilder model)
    {
        model.Entity<Notification>().HasKey(n => n.EventId);
        model.Entity<Notification>().HasOne<ProcessedEvent>().WithOne().HasForeignKey<Notification>(n => n.EventId);
    }

    public async Task StoreAsync(TransferCompleted message, CancellationToken cancellationToken)
    {
        await using var transaction = await Database.BeginTransactionAsync(cancellationToken);
        var now = DateTimeOffset.UtcNow;
        var inserted = await Database.ExecuteSqlInterpolatedAsync($"""
            INSERT INTO "ProcessedEvents" ("Id", "ProcessedAt") VALUES ({message.EventId}, {now})
            ON CONFLICT ("Id") DO NOTHING
            """, cancellationToken);

        if (inserted == 1)
        {
            Notifications.Add(new Notification(message.EventId, message.TransferId, message.DestinationAccountId,
                message.AmountMinor, message.Currency, now));
            await SaveChangesAsync(cancellationToken);
        }

        await transaction.CommitAsync(cancellationToken);
    }
}

public sealed record ProcessedEvent(Guid Id, DateTimeOffset ProcessedAt);
public sealed record Notification(Guid EventId, Guid TransferId, Guid RecipientAccountId,
    long AmountMinor, string Currency, DateTimeOffset CreatedAt);

