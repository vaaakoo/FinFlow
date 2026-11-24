namespace FinFlow.Payments.Api;

public static class SimulatedProvider
{
    public static readonly string[] Scenarios =
        ["success", "decline", "delay", "timeout", "unavailable", "duplicate", "uncertain"];

    public static async Task<SettlementReceipt[]> SettleAsync(Payment payment, CancellationToken token)
    {
        if (payment.Scenario == "delay")
            await Task.Delay(TimeSpan.FromSeconds(2), token);

        if (payment.FailedAttempts == 0)
        {
            if (payment.Scenario is "timeout" or "uncertain")
                throw new TimeoutException("Simulated provider did not return an outcome.");
            if (payment.Scenario == "unavailable")
                throw new HttpRequestException("Simulated provider is temporarily unavailable.");
        }

        var receipt = new SettlementReceipt(payment.Id, payment.Scenario != "decline");
        if (payment.Scenario == "duplicate")
            return [receipt, receipt];
        return [receipt];
    }
}

public sealed record SettlementReceipt(Guid PaymentId, bool Approved);
