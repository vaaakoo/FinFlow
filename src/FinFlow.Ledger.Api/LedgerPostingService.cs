using FinFlow.Ledger.Contracts;
using Grpc.Core;

namespace FinFlow.Ledger.Api;

public sealed class LedgerPostingService(LedgerDb db) : LedgerPosting.LedgerPostingBase
{
    public override async Task<PostingOutcome> Post(PostTransfer request, ServerCallContext context)
    {
        if (!Guid.TryParse(request.OperationId, out var operationId) || operationId == Guid.Empty
            || !Guid.TryParse(request.SourceAccountId, out var sourceId)
            || !Guid.TryParse(request.DestinationAccountId, out var destinationId)
            || request.OwnerSubject.Length is < 1 or > 128)
            throw new RpcException(new Status(StatusCode.InvalidArgument, "Invalid posting request."));

        var outcome = await Transfers.PostAsync(operationId.ToString(), "transfer-service", request.OwnerSubject,
            new TransferRequest(sourceId, destinationId, request.AmountMinor), db, context.CancellationToken);
        return outcome.Transfer is { } transfer
            ? new PostingOutcome { TransferId = transfer.Id.ToString() }
            : new PostingOutcome { Rejection = outcome.Error! };
    }
}

