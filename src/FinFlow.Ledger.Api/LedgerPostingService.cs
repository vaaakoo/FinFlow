using FinFlow.Ledger.Contracts;
using FinFlow.Authentication;
using Grpc.Core;
using Microsoft.AspNetCore.Authorization;
using Microsoft.EntityFrameworkCore;

namespace FinFlow.Ledger.Api;

public sealed class LedgerPostingService : LedgerPosting.LedgerPostingBase
{
    private readonly LedgerDb db;

    public LedgerPostingService(LedgerDb db)
    {
        this.db = db;
    }

    [Authorize(Policy = FinFlowIdentity.LedgerPosterPolicy)]
    public override async Task<PostingOutcome> Post(PostTransfer request, ServerCallContext context)
    {
        if (!Guid.TryParse(request.OperationId, out var operationId) || operationId == Guid.Empty
            || !Guid.TryParse(request.SourceAccountId, out var sourceId)
            || !Guid.TryParse(request.DestinationAccountId, out var destinationId)
            || request.OwnerSubject.Length is < 1 or > 128)
            throw new RpcException(new Status(StatusCode.InvalidArgument, "Invalid posting request."));

        var outcome = await Transfers.PostAsync(operationId.ToString(), "transfer-service", request.OwnerSubject,
            new TransferRequest(sourceId, destinationId, request.AmountMinor), db, context.CancellationToken);
        return ToReply(outcome);
    }

    [Authorize(Policy = FinFlowIdentity.PaymentPosterPolicy)]
    public override async Task<PostingOutcome> Charge(ChargePayment request, ServerCallContext context)
    {
        if (!Guid.TryParse(request.PaymentId, out var paymentId) || paymentId == Guid.Empty
            || !Guid.TryParse(request.SourceAccountId, out var sourceId)
            || request.OwnerSubject.Length is < 1 or > 128)
            throw new RpcException(new Status(StatusCode.InvalidArgument, "Invalid payment request."));

        var outcome = await Transfers.PostAsync(paymentId.ToString(), Transfers.PaymentCharge, request.OwnerSubject,
            new TransferRequest(sourceId, LedgerDb.PaymentSettlement, request.AmountMinor), db, context.CancellationToken);
        return ToReply(outcome);
    }

    [Authorize(Policy = FinFlowIdentity.PaymentPosterPolicy)]
    public override async Task<PostingOutcome> Refund(RefundPayment request, ServerCallContext context)
    {
        if (!Guid.TryParse(request.PaymentId, out var paymentId) || paymentId == Guid.Empty)
            throw new RpcException(new Status(StatusCode.InvalidArgument, "Invalid payment ID."));

        var key = paymentId.ToString();
        var charge = await db.Transfers.AsNoTracking().SingleOrDefaultAsync(
            transfer => transfer.Scope == Transfers.PaymentCharge && transfer.IdempotencyKey == key,
            context.CancellationToken);
        if (charge is null)
            return new PostingOutcome { Rejection = "Payment charge not found." };

        var outcome = await Transfers.PostAsync(key, Transfers.PaymentRefund, null,
            new TransferRequest(charge.DestinationAccountId, charge.SourceAccountId, charge.AmountMinor),
            db, context.CancellationToken);
        return ToReply(outcome);
    }

    private static PostingOutcome ToReply(Transfers.PostingResult outcome)
    {
        if (outcome.Transfer is null)
            return new PostingOutcome { Rejection = outcome.Error! };

        return new PostingOutcome { TransferId = outcome.Transfer.Id.ToString() };
    }
}

