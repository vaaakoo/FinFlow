using Microsoft.EntityFrameworkCore;

namespace FinFlow.Ledger.Api;

public sealed class LedgerDb : DbContext
{
    public LedgerDb(DbContextOptions<LedgerDb> options) : base(options)
    {
    }

    public static readonly Guid Alice = Guid.Parse("aaaaaaaa-aaaa-aaaa-aaaa-aaaaaaaaaaaa");
    public static readonly Guid Bob = Guid.Parse("bbbbbbbb-bbbb-bbbb-bbbb-bbbbbbbbbbbb");

    public DbSet<Account> Accounts => Set<Account>();
    public DbSet<Transfer> Transfers => Set<Transfer>();
    public DbSet<LedgerEntry> Entries => Set<LedgerEntry>();

    public DbSet<OutboxMessage> Outbox => Set<OutboxMessage>();

    protected override void OnModelCreating(ModelBuilder model)
    {
        model.Entity<OutboxMessage>().HasIndex(e => new { e.CreatedAt, e.Id }).HasFilter(@"""PublishedAt"" IS NULL");
        model.Entity<Account>().ToTable(table =>
            table.HasCheckConstraint("CK_Account_Balance", "\"Balance\" >= 0"));
        model.Entity<Account>().Property(a => a.OwnerSubject).HasMaxLength(128);
        model.Entity<Account>().HasIndex(a => new { a.OwnerSubject, a.Id });
        model.Entity<Account>().HasData(
            new Account { Id = Alice, Name = "Alice Demo", Balance = 100_000 },
            new Account { Id = Bob, Name = "Bob Demo", Balance = 50_000 });

        model.Entity<Transfer>().HasIndex(t => new { t.Scope, t.IdempotencyKey }).IsUnique();
        model.Entity<Transfer>().Property(t => t.Scope).HasMaxLength(16).HasDefaultValue("http");
        model.Entity<Transfer>().Property(t => t.IdempotencyKey).HasMaxLength(128);
        model.Entity<Transfer>().HasOne<Account>().WithMany().HasForeignKey(t => t.SourceAccountId);
        model.Entity<Transfer>().HasOne<Account>().WithMany().HasForeignKey(t => t.DestinationAccountId);
        model.Entity<LedgerEntry>().HasKey(e => new { e.TransferId, e.AccountId });
        model.Entity<LedgerEntry>().HasOne<Transfer>().WithMany().HasForeignKey(e => e.TransferId);
        model.Entity<LedgerEntry>().HasOne<Account>().WithMany().HasForeignKey(e => e.AccountId);
    }
}

public sealed class Account
{
    public Guid Id { get; init; }
    public required string Name { get; init; }
    public string? OwnerSubject { get; init; }
    public long Balance { get; set; }
}

public sealed record Transfer(Guid Id, string IdempotencyKey, Guid SourceAccountId,
    Guid DestinationAccountId, long AmountMinor, DateTimeOffset CompletedAt)
{
    public string Scope { get; init; } = "http";
    public TransferReceipt Receipt => new(Id, SourceAccountId, DestinationAccountId, AmountMinor, CompletedAt);
}

public sealed record LedgerEntry(Guid TransferId, Guid AccountId, long DeltaMinor);
public sealed record TransferRequest(Guid SourceAccountId, Guid DestinationAccountId, long AmountMinor);
public sealed record TransferReceipt(Guid Id, Guid SourceAccountId, Guid DestinationAccountId,
    long AmountMinor, DateTimeOffset CompletedAt, string Currency = "GEL");
