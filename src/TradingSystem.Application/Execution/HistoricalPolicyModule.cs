using System.Text.Json;

namespace TradingSystem.Application.Execution;

// Stable JSON-only entry point for an approved archived Application+Domain
// build. It receives no DI container, broker, database, token or live controls.
public sealed class HistoricalPolicyModule
{
    public const int ProtocolVersion = 1;
    public const string ContractSelectionProtocol = RegularOptionContractSelector.Version;
    private readonly IHistoricalReplayPortfolioPolicy policy;

    public HistoricalPolicyModule(string configurationJson, string archiveSliceJson)
    {
        var configuration = JsonSerializer.Deserialize<HistoricalNormalPolicyConfiguration>(configurationJson)
            ?? throw new ArgumentException("Frozen policy configuration is missing.", nameof(configurationJson));
        var slice = JsonSerializer.Deserialize<HistoricalReplayArchiveSlice>(archiveSliceJson)
            ?? throw new ArgumentException("Frozen session input is missing.", nameof(archiveSliceJson));
        if (slice.Session.Bars.Count != 0 || slice.Session.Quotes.Count != 0 ||
            slice.Session.OptionChains.Count != 0 || slice.Session.ControlEvents.Count != 0)
            throw new ArgumentException("A policy constructor may receive pre-session metadata only.", nameof(archiveSliceJson));
        policy = new HistoricalNormalPolicyFactory(configuration).Create(slice);
    }

    public string CodeVersion => policy.CodeVersion;
    public string ConfigurationManifest => policy.ConfigurationManifest;
    public bool ProductionPolicyParityVerified => policy.ProductionPolicyParityVerified;

    public string Query(string operation, string frameJson, string? subjectJson)
    {
        if (!ReferenceEquals(previousFrameJson, frameJson))
        {
            previousFrame = JsonSerializer.Deserialize<HistoricalReplayPortfolioFrame>(frameJson)
                ?? throw new ArgumentException("An as-of frame is required.", nameof(frameJson));
            previousFrameJson = frameJson;
        }
        var frame = previousFrame!;
        return operation switch
        {
            "entries" => JsonSerializer.Serialize(policy.Evaluate(frame)),
            "stops" => JsonSerializer.Serialize(policy.EvaluateStopUpdates(frame)),
            "partials" => JsonSerializer.Serialize(policy.EvaluatePartialExits(frame)),
            "addons" => JsonSerializer.Serialize(policy.EvaluateAddOns(frame)),
            "exits" => JsonSerializer.Serialize(policy.EvaluateExits(frame)),
            "extend" => JsonSerializer.Serialize(policy.ShouldExtendTarget(frame,
                JsonSerializer.Deserialize<HistoricalReplayOpenPosition>(subjectJson ?? "null")
                    ?? throw new ArgumentException("A position is required.", nameof(subjectJson)))),
            "admit" => Admit(frame, subjectJson),
            _ => throw new ArgumentException("Unknown replay policy operation.", nameof(operation))
        };
    }

    private string? previousFrameJson;
    private HistoricalReplayPortfolioFrame? previousFrame;

    private string Admit(HistoricalReplayPortfolioFrame frame, string? subjectJson)
    {
        var input = JsonSerializer.Deserialize<HistoricalModuleAdmission>(subjectJson ?? "null")
            ?? throw new ArgumentException("Deferred quote admission is required.", nameof(subjectJson));
        return JsonSerializer.Serialize(policy.AssessDeferredEntry(frame, input.Pending, input.Quote));
    }
}

public sealed record HistoricalModuleAdmission(HistoricalReplayCandidate Pending, HistoricalReplayQuote Quote);

public sealed record HistoricalPolicyBuild(string Id, DateTimeOffset CapturedAtUtc,
    IReadOnlyDictionary<string, string> AssemblyHashes);

public interface IHistoricalPolicyModuleFactory : IDisposable
{
    string BuildId { get; }
    string? ContractSelectionVersion => null;
    IHistoricalReplayPortfolioPolicy Create(HistoricalNormalPolicyConfiguration configuration,
        HistoricalReplayArchiveSlice slice);
}

public interface IHistoricalPolicyBuildStore
{
    Task<HistoricalPolicyBuild> CaptureCurrentAsync(CancellationToken cancellationToken);
    Task<IReadOnlyList<HistoricalPolicyBuild>> ListAsync(CancellationToken cancellationToken);
    Task<IHistoricalPolicyModuleFactory> LoadAsync(string buildId, CancellationToken cancellationToken);
}
