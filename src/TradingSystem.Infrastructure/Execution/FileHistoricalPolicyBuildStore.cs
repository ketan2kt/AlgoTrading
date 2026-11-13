using System.Reflection;
using System.Runtime.ExceptionServices;
using System.Runtime.Loader;
using System.Security.Cryptography;
using System.Text.Json;
using TradingSystem.Application.Execution;
using TradingSystem.Domain.Trading;
using TradingSystem.Infrastructure.MarketData;

namespace TradingSystem.Infrastructure.Execution;

// Preserve only approved application/domain assemblies from this deployment.
// No code-upload endpoint exists. Archived policies get JSON frames in an
// isolated load context, never the application's service provider or secrets.
internal sealed class FileHistoricalPolicyBuildStore(TimeProvider clock, string? directory = null, ReplayBlobStorage? cloud = null)
    : IHistoricalPolicyBuildStore, IDisposable
{
    private readonly string root = directory ?? Path.Combine(
        Environment.GetEnvironmentVariable("HOME") ?? AppContext.BaseDirectory,
        "data", "historical-replay", "policies");
    private readonly SemaphoreSlim gate = new(1, 1);
    private static readonly string[] AssemblyNames = ["TradingSystem.Application.dll", "TradingSystem.Domain.dll"];

    public async Task<HistoricalPolicyBuild> CaptureCurrentAsync(CancellationToken cancellationToken)
    {
        var application = typeof(HistoricalPolicyModule).Assembly;
        var id = application.ManifestModule.ModuleVersionId.ToString("N");
        await gate.WaitAsync(cancellationToken);
        try
        {
            var path = Path.Combine(root, id);
            if (File.Exists(Path.Combine(path, "manifest.json")))
                return await VerifyAsync(id, cancellationToken);
            Directory.CreateDirectory(path);
            var hashes = new Dictionary<string, string>(StringComparer.Ordinal);
            foreach (var assembly in new[] { application, typeof(Signal).Assembly })
            {
                if (string.IsNullOrWhiteSpace(assembly.Location))
                    throw new InvalidOperationException("This build has no archiveable policy assembly location.");
                var bytes = await File.ReadAllBytesAsync(assembly.Location, cancellationToken);
                var name = Path.GetFileName(assembly.Location);
                if (!AssemblyNames.Contains(name, StringComparer.Ordinal))
                    throw new InvalidOperationException("Unexpected research policy assembly.");
                var destination = Path.Combine(path, name);
                if (File.Exists(destination))
                {
                    var existing = await File.ReadAllBytesAsync(destination, cancellationToken);
                    if (!existing.AsSpan().SequenceEqual(bytes))
                        throw new InvalidDataException("An existing approved policy artifact differs from this build.");
                }
                else await WriteAtomicAsync(destination, bytes, cancellationToken);
                hashes[name] = Convert.ToHexString(SHA256.HashData(bytes));
            }
            var manifest = new HistoricalPolicyBuild(id, clock.GetUtcNow(), hashes);
            await WriteAtomicAsync(Path.Combine(path, "manifest.json"),
                JsonSerializer.SerializeToUtf8Bytes(manifest), cancellationToken);
            return manifest;
        }
        finally { gate.Release(); }
    }

    public async Task<IReadOnlyList<HistoricalPolicyBuild>> ListAsync(CancellationToken cancellationToken)
    {
        if (cloud?.Enabled == true) await cloud.HydratePolicyCatalogueAsync(cancellationToken);
        var builds = new List<HistoricalPolicyBuild>();
        var roots = new[] { root, cloud is null ? root : Path.Combine(cloud.CacheRoot, "policies") };
        foreach (var path in roots.Distinct().Where(Directory.Exists).SelectMany(Directory.EnumerateDirectories))
        {
            var id = Path.GetFileName(path);
            if (!Guid.TryParseExact(id, "N", out _) || !File.Exists(Path.Combine(path, "manifest.json"))) continue;
            // Listing is metadata only. Loading a selected build still verifies
            // both assembly hashes and the compiled module's identity.
            try { builds.Add(await ReadManifestAsync(path, id, cancellationToken)); }
            catch (Exception exception) when (exception is InvalidDataException or JsonException) { }
        }
        return builds.DistinctBy(value => value.Id).OrderByDescending(value => value.CapturedAtUtc).ToArray();
    }

    public async Task<IHistoricalPolicyModuleFactory> LoadAsync(string buildId, CancellationToken cancellationToken)
    {
        if (!Guid.TryParseExact(buildId, "N", out var parsed) || buildId != parsed.ToString("N"))
            throw new ArgumentException("Invalid archived build identifier.", nameof(buildId));
        // Local approved builds already carry their own assembly checksums.
        // For an older remote build, fetch that build only, not the whole catalogue.
        if (cloud?.Enabled == true && !File.Exists(Path.Combine(root, buildId, "manifest.json")))
            await cloud.HydratePolicyAsync(buildId, cancellationToken);
        var build = await VerifyAsync(buildId, cancellationToken);
        return new ArchivedFactory(BuildPath(build.Id), build.Id);
    }

    private string BuildPath(string id) => Directory.Exists(Path.Combine(root, id)) || cloud is null
        ? Path.Combine(root, id) : Path.Combine(cloud.CacheRoot, "policies", id);

    private async Task<HistoricalPolicyBuild> VerifyAsync(string id, CancellationToken token)
    {
        if (!Guid.TryParseExact(id, "N", out var parsed) || id != parsed.ToString("N"))
            throw new ArgumentException("Invalid archived build identifier.", nameof(id));
        var path = BuildPath(id);
        var build = await ReadManifestAsync(path, id, token);
        foreach (var name in AssemblyNames)
        {
            var file = new FileInfo(Path.Combine(path, name));
            if (!file.Exists || file.LinkTarget is not null || file.Length > 32 * 1024 * 1024)
                throw new InvalidDataException("An approved policy assembly is absent or invalid.");
            var hash = Convert.ToHexString(SHA256.HashData(await File.ReadAllBytesAsync(file.FullName, token)));
            if (hash != build.AssemblyHashes[name]) throw new InvalidDataException("Policy assembly integrity check failed.");
        }
        return build;
    }

    private static async Task<HistoricalPolicyBuild> ReadManifestAsync(string path, string id, CancellationToken token)
    {
        if (new DirectoryInfo(path).LinkTarget is not null)
            throw new InvalidDataException("Linked policy artifact paths are not permitted.");
        var manifestPath = Path.Combine(path, "manifest.json");
        if (!File.Exists(manifestPath) || new FileInfo(manifestPath).Length > 64 * 1024 ||
            new FileInfo(manifestPath).LinkTarget is not null)
            throw new InvalidDataException("The approved policy manifest is absent or invalid.");
        var build = JsonSerializer.Deserialize<HistoricalPolicyBuild>(await File.ReadAllTextAsync(manifestPath, token));
        if (build?.Id != id || build.AssemblyHashes is null || build.AssemblyHashes.Count != AssemblyNames.Length ||
            AssemblyNames.Any(name => !build.AssemblyHashes.ContainsKey(name)))
            throw new InvalidDataException("The approved policy manifest does not match its identity.");
        return build;
    }

    private static async Task WriteAtomicAsync(string path, byte[] bytes, CancellationToken token)
    {
        var temporary = path + "." + Guid.NewGuid().ToString("N") + ".pending";
        await File.WriteAllBytesAsync(temporary, bytes, token);
        File.Move(temporary, path, overwrite: false);
    }

    public void Dispose() => gate.Dispose();

    private sealed class PolicyLoadContext(string directory) : AssemblyLoadContext(isCollectible: true)
    {
        protected override Assembly? Load(AssemblyName assemblyName)
        {
            var name = assemblyName.Name ?? string.Empty;
            if (name == "TradingSystem.Domain")
            {
                using var bytes = new MemoryStream(File.ReadAllBytes(Path.Combine(directory, name + ".dll")));
                return LoadFromStream(bytes);
            }
            if (name.StartsWith("TradingSystem.", StringComparison.Ordinal))
                throw new InvalidOperationException("Research modules cannot load infrastructure or execution hosts.");
            return null;
        }
    }

    private sealed class ArchivedFactory : IHistoricalPolicyModuleFactory
    {
        private readonly PolicyLoadContext context;
        private readonly Type type;
        public string BuildId { get; }
        public ArchivedFactory(string directory, string buildId)
        {
            BuildId = buildId;
            context = new PolicyLoadContext(Path.GetFullPath(directory));
            using var bytes = new MemoryStream(File.ReadAllBytes(Path.Combine(directory, AssemblyNames[0])));
            var assembly = context.LoadFromStream(bytes);
            if (assembly.ManifestModule.ModuleVersionId.ToString("N") != buildId)
                throw new InvalidDataException("The archived module identity does not match the approved build.");
            type = assembly.GetType("TradingSystem.Application.Execution.HistoricalPolicyModule", throwOnError: true)!;
            if ((int?)type.GetField("ProtocolVersion")?.GetRawConstantValue() != 1)
                throw new InvalidDataException("Unsupported historical policy module protocol.");
        }

        public string? ContractSelectionVersion => (string?)type.GetField("ContractSelectionProtocol")?.GetRawConstantValue();

        public IHistoricalReplayPortfolioPolicy Create(HistoricalNormalPolicyConfiguration configuration,
            HistoricalReplayArchiveSlice slice)
        {
            // The constructor gets only pre-session metadata. In particular,
            // never hand proposed code the day's future bars/quotes/commands.
            var metadata = new HistoricalReplayArchiveSlice(new(slice.Session.Date, slice.Session.IndexSymbol,
                [], [], slice.Session.StartUtc, slice.Session.EndUtc), slice.PreviousClose, slice.FrontFutureSymbol,
                slice.OptionContracts, [])
            {
                UnderlyingInstrumentId = slice.UnderlyingInstrumentId,
                PriorSensexThesis = slice.PriorSensexThesis,
                InstrumentMasterCapturedAtUtc = slice.InstrumentMasterCapturedAtUtc,
                InstrumentMasterSnapshotFingerprint = slice.InstrumentMasterSnapshotFingerprint
            };
            var instance = Activator.CreateInstance(type, JsonSerializer.Serialize(configuration),
                JsonSerializer.Serialize(metadata)) ?? throw new InvalidDataException("Archived policy creation failed.");
            return new JsonPolicy(type, instance);
        }
        public void Dispose() => context.Unload();
    }

    private sealed class JsonPolicy(Type type, object instance) : IHistoricalReplayPortfolioPolicy
    {
        private readonly MethodInfo query = type.GetMethod("Query") ?? throw new InvalidDataException("Policy query is missing.");
        private HistoricalReplayPortfolioFrame? lastFrame;
        private string lastFrameJson = string.Empty;
        public string CodeVersion => (string)type.GetProperty("CodeVersion")!.GetValue(instance)!;
        public string ConfigurationManifest => (string)type.GetProperty("ConfigurationManifest")!.GetValue(instance)!;
        public bool ProductionPolicyParityVerified => (bool)type.GetProperty("ProductionPolicyParityVerified")!.GetValue(instance)!;
        public IReadOnlyList<HistoricalReplayCandidate> Evaluate(HistoricalReplayPortfolioFrame frame) => Value<HistoricalReplayCandidate[]>("entries", frame);
        public IReadOnlyList<HistoricalReplayStopUpdate> EvaluateStopUpdates(HistoricalReplayPortfolioFrame frame) => Value<HistoricalReplayStopUpdate[]>("stops", frame);
        public IReadOnlyList<HistoricalReplayPartialExit> EvaluatePartialExits(HistoricalReplayPortfolioFrame frame) => Value<HistoricalReplayPartialExit[]>("partials", frame);
        public IReadOnlyList<HistoricalReplayAddOn> EvaluateAddOns(HistoricalReplayPortfolioFrame frame) => Value<HistoricalReplayAddOn[]>("addons", frame);
        public IReadOnlyList<HistoricalReplayExitRequest> EvaluateExits(HistoricalReplayPortfolioFrame frame) => Value<HistoricalReplayExitRequest[]>("exits", frame);
        public bool ShouldExtendTarget(HistoricalReplayPortfolioFrame frame, HistoricalReplayOpenPosition position) => Value<bool>("extend", frame, position);
        public HistoricalReplayCandidate? AssessDeferredEntry(HistoricalReplayPortfolioFrame frame,
            HistoricalReplayCandidate pending, HistoricalReplayQuote optionQuote) =>
            JsonSerializer.Deserialize<HistoricalReplayCandidate>(Invoke("admit", frame, new HistoricalModuleAdmission(pending, optionQuote)));

        private T Value<T>(string operation, HistoricalReplayPortfolioFrame frame, object? subject = null) =>
            JsonSerializer.Deserialize<T>(Invoke(operation, frame, subject)) is { } value ? value
                : throw new InvalidDataException("An archived policy returned an empty result.");
        private string Invoke(string operation, HistoricalReplayPortfolioFrame frame, object? subject)
        {
            if (!ReferenceEquals(lastFrame, frame))
            {
                lastFrame = frame;
                lastFrameJson = JsonSerializer.Serialize(frame);
            }
            try { return (string)query.Invoke(instance, [operation, lastFrameJson,
                subject is null ? null : JsonSerializer.Serialize(subject)])!; }
            catch (TargetInvocationException exception) when (exception.InnerException is not null)
            {
                ExceptionDispatchInfo.Capture(exception.InnerException).Throw();
                throw;
            }
        }
    }
}
