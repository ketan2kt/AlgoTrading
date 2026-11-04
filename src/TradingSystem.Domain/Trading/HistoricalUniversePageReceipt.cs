using TradingSystem.Domain.Common;

namespace TradingSystem.Domain.Trading;

// Research-only proof that every symbol on one verified historical universe
// page was fetched. It does not attest lot metadata or executable quotes.
public sealed class HistoricalUniversePageReceipt : Entity, IAppendOnlyEntity
{
    public HistoricalUniversePageReceipt(Guid id, DateOnly sessionDate, string market,
        DateOnly expiryDate, int page, int pageSize, int totalSelectedContracts,
        string universeFingerprint, string fetchedSymbolsJson,
        DateTimeOffset fetchedAtUtc) : base(id)
    {
        if (sessionDate == default || expiryDate < sessionDate ||
            string.IsNullOrWhiteSpace(market) || page < 0 || pageSize is < 1 or > 20 ||
            totalSelectedContracts < 0 || string.IsNullOrWhiteSpace(universeFingerprint) ||
            string.IsNullOrWhiteSpace(fetchedSymbolsJson))
            throw new ArgumentException("A historical universe page receipt is invalid.");
        SessionDate = sessionDate;
        Market = market;
        ExpiryDate = expiryDate;
        Page = page;
        PageSize = pageSize;
        TotalSelectedContracts = totalSelectedContracts;
        UniverseFingerprint = universeFingerprint;
        FetchedSymbolsJson = fetchedSymbolsJson;
        FetchedAtUtc = fetchedAtUtc.ToUniversalTime();
    }

    public DateOnly SessionDate { get; private init; }
    public string Market { get; private init; }
    public DateOnly ExpiryDate { get; private init; }
    public int Page { get; private init; }
    public int PageSize { get; private init; }
    public int TotalSelectedContracts { get; private init; }
    public string UniverseFingerprint { get; private init; }
    public string FetchedSymbolsJson { get; private init; }
    public DateTimeOffset FetchedAtUtc { get; private init; }
}
