namespace TradingSystem.Application.Execution;

public enum PaperOptionTransactionSide { Buy, Sell }

public sealed record PaperOptionTransaction(PaperOptionTransactionSide Side, decimal Price,
    int Quantity);

public sealed record PaperTradingCostBreakdown(string ScheduleVersion, decimal Brokerage,
    decimal SecuritiesTransactionTax, decimal ExchangeTransactionCharges,
    decimal InvestorProtectionFund, decimal SebiTurnoverFees, decimal GoodsAndServicesTax,
    decimal StampDuty, decimal Total)
{
    public static PaperTradingCostBreakdown Empty { get; } =
        new("none", 0m, 0m, 0m, 0m, 0m, 0m, 0m, 0m);
}

public static class PaperTradingCostModel
{
    // Groww/NSE equity-option schedule effective 1 April 2026. Contract notes remain authoritative.
    public const string ScheduleVersion = "GROWW-NSE-OPTIONS-2026-04-01";
    private const decimal BrokeragePerExecutedOrder = 20m;
    private const decimal SttSellRate = 0.0015m;
    private const decimal ExchangeRate = 0.0003503m;
    private const decimal InvestorProtectionFundRate = 0.000005m;
    private const decimal SebiRate = 0.000001m;
    private const decimal GstRate = 0.18m;
    private const decimal StampDutyBuyRate = 0.00003m;

    public static PaperTradingCostBreakdown CalculateOptionCharges(
        IEnumerable<PaperOptionTransaction> transactions)
    {
        var values = transactions.ToArray();
        if (values.Length == 0) return PaperTradingCostBreakdown.Empty;
        if (values.Any(value => value.Price <= 0 || value.Quantity <= 0))
            throw new ArgumentOutOfRangeException(nameof(transactions));

        var buyTurnover = values.Where(value => value.Side == PaperOptionTransactionSide.Buy)
            .Sum(value => value.Price * value.Quantity);
        var sellTurnover = values.Where(value => value.Side == PaperOptionTransactionSide.Sell)
            .Sum(value => value.Price * value.Quantity);
        var turnover = buyTurnover + sellTurnover;
        var brokerage = BrokeragePerExecutedOrder * values.Length;
        var stt = sellTurnover * SttSellRate;
        var exchange = turnover * ExchangeRate;
        var ipft = turnover * InvestorProtectionFundRate;
        var sebi = turnover * SebiRate;
        var gst = (brokerage + exchange + ipft + sebi) * GstRate;
        var stamp = buyTurnover * StampDutyBuyRate;
        var total = brokerage + stt + exchange + ipft + sebi + gst + stamp;
        return new(ScheduleVersion, Round(brokerage), Round(stt), Round(exchange), Round(ipft),
            Round(sebi), Round(gst), Round(stamp), Round(total));
    }

    private static decimal Round(decimal value) =>
        decimal.Round(value, 2, MidpointRounding.AwayFromZero);
}
