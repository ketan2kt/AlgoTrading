namespace TradingSystem.Application.MarketData;

public sealed record PreSessionSourceData(
    string Market,
    DateOnly SessionDate,
    string Stage,
    DateTimeOffset CapturedAtUtc,
    decimal? PreviousOpen,
    decimal? PreviousClose,
    decimal? PreviousHigh,
    decimal? PreviousLow,
    DateTimeOffset? PreviousSessionAtUtc,
    decimal? IndicativePrice,
    DateTimeOffset? IndicativePriceAtUtc,
    decimal? CallOiStrike,
    decimal? PutOiStrike,
    DateOnly? OptionExpiry,
    DateTimeOffset? OptionOiAtUtc);

public static class PreSessionBriefBuilder
{
    public static PreSessionMarketBrief Build(PreSessionSourceData data)
    {
        var hasLevels = data.PreviousHigh is > 0 && data.PreviousLow is > 0 &&
                        data.PreviousHigh >= data.PreviousLow;
        var hasIndicative = data.IndicativePrice is > 0 && data.IndicativePriceAtUtc is not null;
        var judgement = "Unclear";
        var reason = "No sufficiently strong prior-session directional close or verified pre-open indication.";
        if (hasLevels && data.PreviousOpen is > 0 && data.PreviousClose is > 0 &&
            data.PreviousHigh > data.PreviousLow)
        {
            var range = data.PreviousHigh.Value - data.PreviousLow.Value;
            var body = data.PreviousClose.Value - data.PreviousOpen.Value;
            if (body >= range * .55m && data.PreviousHigh.Value - data.PreviousClose.Value <= range * .25m)
            {
                judgement = "Up";
                reason = "Provisional continuation hypothesis: prior session closed strongly near its high.";
            }
            else if (-body >= range * .55m && data.PreviousClose.Value - data.PreviousLow.Value <= range * .25m)
            {
                judgement = "Down";
                reason = "Provisional continuation hypothesis: prior session closed strongly near its low.";
            }
        }
        if (hasLevels && hasIndicative)
        {
            var gapJudgement = data.IndicativePrice > data.PreviousHigh ? "Up" :
                data.IndicativePrice < data.PreviousLow ? "Down" : "Unclear";
            if (gapJudgement != "Unclear" && judgement != "Unclear" && judgement != gapJudgement)
            {
                judgement = "Unclear";
                reason = "Prior-session momentum conflicts with the indicative gap.";
            }
            else if (gapJudgement != "Unclear")
            {
                judgement = gapJudgement;
                reason = "Provisional gap hypothesis from a timestamped indicative price; live validation required.";
            }
        }
        var bias = "Unclear";
        var headline = "Opening direction needs confirmation after 9:15 IST.";
        if (hasLevels && hasIndicative)
        {
            if (data.IndicativePrice > data.PreviousHigh)
            {
                bias = "Upside gap watch";
                headline = "Indicative price is above the previous high; watch for a hold or gap failure.";
            }
            else if (data.IndicativePrice < data.PreviousLow)
            {
                bias = "Downside gap watch";
                headline = "Indicative price is below the previous low; watch for continuation or reclaim.";
            }
            else
            {
                bias = "Inside-range watch";
                headline = "Indicative price is inside the previous range; wait for price discovery.";
            }
        }

        string[] scenarios = hasLevels
            ? new[]
            {
                $"Above {data.PreviousHigh:0.##}: assess a sustained break, not the first print; return below it invalidates the breakout idea.",
                $"Below {data.PreviousLow:0.##}: assess a sustained break; reclaim above it invalidates the downside idea.",
                $"Inside {data.PreviousLow:0.##}–{data.PreviousHigh:0.##}: treat direction as unresolved until the opening range and live volume agree."
            }
            : ["Previous-session levels are unavailable; do not infer an opening direction from this brief."];

        var evidence = new List<PreSessionEvidence>
        {
            new("Previous-session index range", hasLevels ? "PreviousSession" : "Unavailable",
                hasLevels ? $"{data.PreviousLow:0.##}–{data.PreviousHigh:0.##}; close {data.PreviousClose:0.##}" : null,
                hasLevels ? "Groww recorded index candles" : null, data.PreviousSessionAtUtc,
                hasLevels ? "Completed previous session only; not today's price." : "No complete recorded prior session."),
            new("Option OI concentration", data.OptionOiAtUtc is not null ? "PreviousSession" : "Unavailable",
                data.OptionOiAtUtc is not null
                    ? $"Call {data.CallOiStrike?.ToString("0.##") ?? "—"}; put {data.PutOiStrike?.ToString("0.##") ?? "—"}; expiry {data.OptionExpiry}"
                    : null,
                data.OptionOiAtUtc is not null ? "Groww recorded option-chain sample" : null,
                data.OptionOiAtUtc,
                "Sampled strikes from the last recorded chain; this is not current-session OI buildup."),
            new("Indian pre-open", hasIndicative ? "Indicative" : "Unavailable",
                hasIndicative ? data.IndicativePrice?.ToString("0.##") : null,
                hasIndicative ? "Authorized pre-open feed" : null, data.IndicativePriceAtUtc,
                hasIndicative ? "Indicative auction value; confirm after the continuous market opens."
                    : "No verified pre-open feed is connected."),
        };
        foreach (var name in new[] { "GIFT Nifty", "US and Asian markets", "Crude oil", "Bond yields and USD/INR", "FII/DII prior-session flows", "Scheduled events" })
            evidence.Add(new(name, "Unavailable", null, null, null,
                "No authorized timestamped provider is connected; excluded from the directional assessment."));

        var limitations = new List<string>
        {
            "Read-only research: this brief never changes signals, risk controls, order routing, or trade size.",
            "Previous-session OI and levels are context, not evidence of today's buying or selling.",
            "A gap scenario must be checked against the live opening range and can be invalidated."
        };
        if (!hasIndicative) limitations.Add("No validated pre-open indicative price is available; no directional gap call is made.");

        return new PreSessionMarketBrief(data.Market, data.SessionDate, data.Stage,
            data.CapturedAtUtc, bias, headline, judgement, "Low", reason,
            data.PreviousOpen, data.PreviousClose, data.PreviousHigh,
            data.PreviousLow, data.PreviousSessionAtUtc, data.IndicativePrice,
            data.IndicativePriceAtUtc, data.CallOiStrike, data.PutOiStrike,
            data.OptionExpiry, data.OptionOiAtUtc, scenarios, evidence, limitations);
    }
}
