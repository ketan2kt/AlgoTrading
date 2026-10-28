using System.Globalization;

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
    DateTimeOffset? OptionOiAtUtc,
    IReadOnlyList<PreSessionHistoricalSession>? RecentSessions = null,
    decimal? PreOpenQuote = null,
    DateTimeOffset? PreOpenQuoteAtUtc = null,
    EcbUsdInrReference? UsdInrReference = null,
    TreasuryTenYearYield? UsTenYearYield = null,
    FedMonetaryRelease? RecentFedRelease = null,
    IReadOnlyList<PreSessionEvidence>? ExternalEvidence = null);

public sealed record PreSessionHistoricalSession(DateOnly Date, decimal Open,
    decimal High, decimal Low, decimal Close, int CandleCount);

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
                    ? $"Call {data.CallOiStrike?.ToString("0.##", CultureInfo.InvariantCulture) ?? "—"}; put {data.PutOiStrike?.ToString("0.##", CultureInfo.InvariantCulture) ?? "—"}; expiry {data.OptionExpiry}"
                    : null,
                data.OptionOiAtUtc is not null ? "Groww recorded option-chain sample" : null,
                data.OptionOiAtUtc,
                "Sampled strikes from the last recorded chain; this is not current-session OI buildup."),
            new("Indian pre-open", hasIndicative ? "Indicative" : "Unavailable",
                hasIndicative ? data.IndicativePrice?.ToString("0.##", CultureInfo.InvariantCulture) : null,
                hasIndicative ? "Authorized pre-open feed" : null, data.IndicativePriceAtUtc,
                hasIndicative ? "Indicative auction value; confirm after the continuous market opens."
                    : "No verified pre-open feed is connected."),
        };
        var history = data.RecentSessions?.OrderBy(value => value.Date).ToArray() ?? [];
        if (history.Length >= 3)
        {
            var first = history[0];
            var last = history[^1];
            var change = first.Close > 0 ? (last.Close - first.Close) / first.Close * 100m : 0m;
            var averageRange = history.Average(value => value.High - value.Low);
            var latestRange = last.High - last.Low;
            evidence.Add(new("Recent index sessions", "PreviousSession",
                $"{history.Length} sessions; close change {change:+0.##;-0.##;0}%; " +
                $"latest range {latestRange:0.##} vs average {averageRange:0.##}",
                "Groww recorded index candles", data.PreviousSessionAtUtc,
                "Completed historical sessions only; not a current-session signal."));
        }
        if (data.PreOpenQuote is > 0 && data.PreOpenQuoteAtUtc is not null)
        {
            evidence.Add(new("Groww index quote before open", "TimestampedQuote",
                data.PreOpenQuote.Value.ToString("0.##", CultureInfo.InvariantCulture),
                "Groww live-data quote", data.PreOpenQuoteAtUtc,
                "Timestamped current-session quote; not verified as an auction indicative price."));
        }
        if (data.UsdInrReference is { } fx && fx.Date <= data.SessionDate &&
            fx.Date >= data.SessionDate.AddDays(-5))
        {
            evidence.Add(new("USD/INR reference", "PreviousSession",
                fx.RupeesPerDollar.ToString("0.####", CultureInfo.InvariantCulture),
                "ECB euro foreign exchange reference rates", null,
                $"ECB {fx.Date:yyyy-MM-dd} EUR/INR divided by EUR/USD; calculated cross, not a live tradable quote. Source: ECB statistics."));
        }
        if (data.UsTenYearYield is { } treasury && treasury.Date <= data.SessionDate &&
            treasury.Date >= data.SessionDate.AddDays(-5))
        {
            var changeBps = treasury.PreviousPercent is { } previous
                ? $"; change {(treasury.Percent - previous) * 100m:+0.#;-0.#;0} bp"
                : "";
            evidence.Add(new("US 10-year Treasury yield", "PreviousSession",
                $"{treasury.Percent:0.##}%{changeBps}",
                "US Treasury daily par yield curve", null,
                $"Official dated {treasury.Date:yyyy-MM-dd} US yield; not an Indian bond yield or a live quote."));
        }
        if (data.RecentFedRelease is { } fed && fed.PublishedAtUtc <= data.CapturedAtUtc &&
            fed.PublishedAtUtc >= data.CapturedAtUtc.AddHours(-36))
        {
            evidence.Add(new("Recent Federal Reserve monetary release", "Published",
                fed.Title, "Federal Reserve monetary-policy RSS", fed.PublishedAtUtc,
                "Official headline only; assess the full release separately before inferring market impact."));
        }
        var external = data.ExternalEvidence ?? [];
        evidence.AddRange(external);
        foreach (var name in new[] { "GIFT Nifty", "US and Asian markets", "Crude oil", "FII/DII prior-session flows", "Scheduled events" })
            if (!external.Any(value => value.Name == name))
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
