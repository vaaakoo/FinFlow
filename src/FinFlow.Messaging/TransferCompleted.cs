namespace FinFlow.Messaging;

public sealed record TransferCompleted(Guid EventId, Guid TransferId, Guid SourceAccountId,
    Guid DestinationAccountId, long AmountMinor, string Currency, DateTimeOffset CompletedAt,
    string? CorrelationId = null, string? CausationId = null);

