using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Linq;
using System.Reflection;
using System.Runtime.Serialization;
using System.Threading;
using Verse;

namespace RimWorldDevBridge
{
    internal static class BridgeAdapterCatalog
    {
        private const int CircuitBreakFailures = 3;
        private const double LegacySeriousOverrunMs = 250d;
        private const int MaximumAdapterSources = 256;
        private const int MaximumManifestsPerSource = 256;
        private const long MaximumAdapterBytes = 64L * 1024L * 1024L;
        private static readonly object Gate = new object();
        private static readonly Dictionary<string, AdapterGeneration> Active =
            new Dictionary<string, AdapterGeneration>(StringComparer.OrdinalIgnoreCase);
        private static readonly Dictionary<string, AdapterGeneration> CommandsByName =
            new Dictionary<string, AdapterGeneration>(StringComparer.OrdinalIgnoreCase);
        private static readonly List<AdapterGeneration> All = new List<AdapterGeneration>();
        private static readonly List<string> IndexErrors = new List<string>();
        private static HashSet<string> loadedPackages = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        private static List<BridgeAdapterSourceRecord> sourceRecords = new List<BridgeAdapterSourceRecord>();
        private static volatile bool indexing;
        private static string state = "dormant";
        private static string fingerprint = BridgeProtocol.CoreSchema;
        private static int ignoredAssemblyCount;
        private static double indexMs;
        private static long indexGeneration;
        private static long sourceGeneration;
        private static int lastIndexThreadId;

        internal static bool Indexing => indexing;
        internal static string State => state;
        internal static string Fingerprint => fingerprint;
        internal static bool RestartRequired
        {
            get
            {
                lock (Gate)
                {
                    int threshold = RimWorldDevBridgeMod.Settings?.RetainedAdapterRestartThreshold ?? 8;
                    return All.Count(item => item.Assembly != null && !item.RetainedOnly) >= threshold;
                }
            }
        }
        internal static int LastIndexThreadIdForTests => Volatile.Read(ref lastIndexThreadId);
        internal static IEnumerable<BridgeCommandDescriptor> Commands
        {
            get { lock (Gate) return CommandsByName.Keys.OrderBy(value => value)
                .Select(value => DescriptorFor(CommandsByName[value], value)).ToList(); }
        }

        internal static void ActivateIndexing()
        {
            BridgeAdapterSourceDiscovery.Capture(Interlocked.Increment(ref sourceGeneration),
                BridgePaths.AdapterPath, out List<BridgeAdapterSourceRecord> sources,
                out HashSet<string> packages);
            BeginIndex(sources, packages);
        }

        internal static BridgeResult Reindex()
        {
            BridgeAdapterSourceDiscovery.Capture(Interlocked.Increment(ref sourceGeneration),
                BridgePaths.AdapterPath, out List<BridgeAdapterSourceRecord> sources,
                out HashSet<string> packages);
            bool started = BeginIndex(sources, packages);
            return BridgeResult.Ok("core.adapterReindex")
                .Add("started", started)
                .Add("state", State)
                .Add("note", "Manifest parsing and hashing run off the game thread.");
        }

        private static bool BeginIndex(IReadOnlyList<BridgeAdapterSourceRecord> sources,
            IReadOnlyCollection<string> packages)
        {
            long generation;
            lock (Gate)
            {
                indexing = true;
                state = "indexing";
                generation = ++indexGeneration;
                sourceRecords = sources == null ? new List<BridgeAdapterSourceRecord>() : sources.ToList();
            }
            IndexContext context = new IndexContext(generation, sourceRecords,
                new HashSet<string>(packages ?? Enumerable.Empty<string>(), StringComparer.OrdinalIgnoreCase));
            ThreadPool.QueueUserWorkItem(_ => IndexWorker(context, true, 0));
            return true;
        }

        internal static void InvalidateIndexing()
        {
            lock (Gate)
            {
                ++indexGeneration;
                indexing = false;
                state = "dormant";
            }
        }

        internal static BridgeCommandDescriptor Describe(string command)
        {
            string name = BridgeText.NormalizeCommand(command);
            lock (Gate)
            {
                return CommandsByName.TryGetValue(name, out AdapterGeneration generation)
                    ? DescriptorFor(generation, name) : null;
            }
        }

        internal static bool IsAvailable(string provider)
        {
            return IsAvailable(provider, null);
        }

        internal static bool IsAvailable(string provider, string minimumVersion)
        {
            lock (Gate)
            {
                if (!Active.TryGetValue(provider ?? string.Empty, out AdapterGeneration value)) return false;
                if (value.QuarantinedUntilUtc > DateTime.UtcNow) return false;
                if (value.State == "quarantined" && value.QuarantinedUntilUtc != default(DateTime) &&
                    value.QuarantinedUntilUtc <= DateTime.UtcNow)
                    value.State = value.Assembly != null ? "loaded" :
                        value.PreparedBytes != null ? "prepared" : "available";
                return value.State != "failed" && value.State != "quarantined" && value.Compatible &&
                    VersionAtLeast(value.Manifest.version, minimumVersion);
            }
        }

        private static bool VersionAtLeast(string actual, string minimum)
        {
            if (string.IsNullOrWhiteSpace(minimum)) return true;
            if (Version.TryParse(actual, out Version actualVersion) &&
                Version.TryParse(minimum, out Version minimumVersion)) return actualVersion >= minimumVersion;
            return string.Equals(actual, minimum, StringComparison.OrdinalIgnoreCase);
        }

        internal static BridgeResult Prepare(BridgeRequest request)
        {
            AdapterGeneration generation;
            lock (Gate)
            {
                if (!CommandsByName.TryGetValue(BridgeText.NormalizeCommand(request.Command), out generation)) return null;
                if (!generation.Compatible)
                    return BridgeResult.Fail(BridgeStatus.INCOMPATIBLE, "adapter_incompatible", generation.Reason);
                if (generation.State == "failed")
                    return BridgeResult.Fail(BridgeStatus.UNAVAILABLE, "adapter_failed", generation.Reason);
                if (generation.QuarantinedUntilUtc > DateTime.UtcNow)
                    return BridgeResult.Fail(BridgeStatus.UNAVAILABLE, "adapter_circuit_open",
                        generation.QuarantinedUntilUtc.ToString("o"));
                if (generation.State == "quarantined" && generation.QuarantinedUntilUtc != default(DateTime))
                    generation.State = generation.Assembly != null ? "loaded" :
                        generation.PreparedBytes != null ? "prepared" : "available";
                Pin(request, generation);
                if (generation.PreparedBytes != null || generation.PreparedAssembly != null ||
                    generation.Assembly != null) return null;
            }
            if (DateTime.UtcNow >= request.DeadlineUtc)
                return BridgeResult.Fail(BridgeStatus.TIMEOUT, "adapter_prepare_deadline_expired");
            try
            {
                Assembly preparedAssembly = null;
                string assemblyPath = generation.AssemblyPath;
                if (string.Equals(generation.Manifest.assemblySource, "loaded", StringComparison.OrdinalIgnoreCase))
                {
                    List<Assembly> matches = AppDomain.CurrentDomain.GetAssemblies().Where(item =>
                        string.Equals(item.FullName, generation.Manifest.assemblyIdentity,
                            StringComparison.Ordinal)).ToList();
                    if (matches.Count != 1)
                        return BridgeResult.Fail(BridgeStatus.UNAVAILABLE, "loaded_adapter_assembly_missing",
                            matches.Count == 0 ? generation.Manifest.assemblyIdentity :
                            "duplicate loaded assembly identity");
                    preparedAssembly = matches[0];
                    if (!string.IsNullOrWhiteSpace(generation.Manifest.moduleMvid) &&
                        (!Guid.TryParse(generation.Manifest.moduleMvid, out Guid expectedMvid) ||
                        preparedAssembly.ManifestModule.ModuleVersionId != expectedMvid))
                        return BridgeResult.Fail(BridgeStatus.INCOMPATIBLE, "loaded_adapter_mvid_mismatch");
                    if (generation.Source == null || generation.Source.SourceKind != BridgeAdapterSourceKind.OwnerMod ||
                        !string.Equals(generation.Source.PackageId, generation.Manifest.modulePackageId,
                            StringComparison.OrdinalIgnoreCase))
                        return BridgeResult.Fail(BridgeStatus.UNAVAILABLE, "loaded_adapter_package_missing",
                            generation.Manifest.modulePackageId);
                    string root = Path.GetFullPath(generation.Source.OwnerRootPath) + Path.DirectorySeparatorChar;
                    assemblyPath = Path.GetFullPath(Path.Combine(root,
                        generation.Manifest.moduleRelativePath.Replace('/', Path.DirectorySeparatorChar)));
                    if (!BridgeAdapterAssemblyVerification.IsWithin(assemblyPath, generation.Source.OwnerRootPath) ||
                        !BridgeAdapterAssemblyVerification.IsSafeFile(assemblyPath, generation.Source.OwnerRootPath) ||
                        !File.Exists(assemblyPath))
                        return BridgeResult.Fail(BridgeStatus.INCOMPATIBLE, "loaded_adapter_module_missing");
                    string loadedOrigin = BridgeAdapterAssemblyVerification.LoadedAssemblyPath(preparedAssembly);
                    if (!string.IsNullOrEmpty(loadedOrigin) && !string.Equals(loadedOrigin, assemblyPath,
                        StringComparison.OrdinalIgnoreCase) &&
                        !BridgeAdapterAssemblyVerification.IsPrepatcherShadowPath(loadedOrigin))
                        return BridgeResult.Fail(BridgeStatus.INCOMPATIBLE, "loaded_adapter_origin_mismatch");
                }
                byte[] bytes = null;
                if (!string.IsNullOrWhiteSpace(assemblyPath))
                {
                    bytes = BridgeAdapterAssemblyVerification.ReadAllBytesShared(assemblyPath);
                    if (bytes.LongLength != generation.Manifest.assemblyBytes)
                        return FailGeneration(generation, BridgeStatus.INCOMPATIBLE,
                            "adapter_size_mismatch");
                    string hash = BridgeAdapterAssemblyVerification.Hash(bytes);
                    if (!hash.Equals(generation.Manifest.contentHash, StringComparison.OrdinalIgnoreCase))
                        return FailGeneration(generation, BridgeStatus.INCOMPATIBLE, "adapter_hash_mismatch");
                    generation.Verification = "sha256";
                }
                else generation.Verification = "identity-only-mono-location-unavailable";
                lock (Gate)
                {
                    if (generation.Assembly == null)
                    {
                        if (preparedAssembly != null) generation.PreparedAssembly = preparedAssembly;
                        else generation.PreparedBytes = bytes;
                    }
                    generation.State = generation.Assembly == null ? "prepared" : "loaded";
                    Pin(request, generation);
                }
                return null;
            }
            catch (Exception exception)
            {
                return FailGeneration(generation, BridgeStatus.ERROR, "adapter_prepare_failed",
                    exception.GetBaseException().Message);
            }
        }

        internal static BridgeResult Execute(BridgeExecutionContext context)
            => BridgeAdapterExecution.Execute(context, Gate, All, CommandsByName,
                CircuitBreakFailures, LegacySeriousOverrunMs);

        internal static BridgeResult Health()
        {
            BridgeResult result = BridgeResult.Ok("core.adapterHealth");
            lock (Gate)
            {
                int retained = All.Count(item => item.Assembly != null &&
                    !string.Equals(item.Manifest.assemblySource, "loaded", StringComparison.OrdinalIgnoreCase));
                int loadedAssemblyProviders = All.Count(item => item.Assembly != null &&
                    string.Equals(item.Manifest.assemblySource, "loaded", StringComparison.OrdinalIgnoreCase));
                long preparedBytes = All.Where(item => item.PreparedBytes != null)
                    .Sum(item => (long)item.PreparedBytes.Length);
                result.Add("state", state).Add("manifests", All.Count).Add("active", Active.Count)
                    .Add("commands", CommandsByName.Count).Add("retainedGenerations", retained)
                    .Add("estimatedRetainedBytes", All.Where(item => item.Assembly != null &&
                        !string.Equals(item.Manifest.assemblySource, "loaded", StringComparison.OrdinalIgnoreCase))
                        .Sum(item => item.Manifest.assemblyBytes))
                    .Add("loadedAssemblyProviders", loadedAssemblyProviders)
                    .Add("preparedBytes", preparedBytes).Add("ignoredAssemblies", ignoredAssemblyCount)
                    .Add("superseded", All.Count(item => !item.Selected && item.Compatible))
                    .Add("incompatible", All.Count(item => !item.Compatible)).Add("errors", IndexErrors.Count)
                    .Add("indexMs", indexMs);
                foreach (AdapterGeneration item in All.OrderBy(value => value.Manifest.adapterId)
                    .ThenByDescending(value => value.BuildUtc))
                {
                    result.AddLine("adapter=id:" + BridgeText.Clean(item.Manifest.adapterId) + " version:" +
                        BridgeText.Clean(item.Manifest.version) + " generation:" + BridgeText.Clean(item.Manifest.generation) +
                        " state:" + item.State + " selected:" + item.Selected + " compatible:" + item.Compatible +
                        " source:" + BridgeText.Clean(item.Manifest.assemblySource) + " file:" +
                        BridgeText.Clean(item.Manifest.assemblyFile) + " calls:" + item.InvocationCount + " failures:" +
                        item.FailureCount + " lastMs:" + item.LastExecutionMs.ToString("0.###") + " reason:" +
                        BridgeText.Clean(item.Reason ?? item.LastFailure ?? "none") + " loadMs:" +
                        item.LoadMs.ToString("0.###") + " verification:" +
                        BridgeText.Clean(item.Verification ?? "manifest-only") + " contract:" +
                        (string.Equals(item.Manifest.executionContract, "cooperative-v1", StringComparison.OrdinalIgnoreCase)
                            ? "cooperative-v1" : "legacy-sync-non-cooperative") + " seriousOverruns:" +
                        item.SeriousOverruns + " sourceKind:" +
                        (item.Source == null ? "unknown" : item.Source.SourceKind.ToString()) + " sourcePackage:" +
                        BridgeText.Clean(item.Source == null ? "none" : item.Source.PackageId) + " sourceIdentity:" +
                        BridgeText.Clean(item.Source == null ? "unknown" : item.Source.DisplayIdentity));
                }
                foreach (string error in IndexErrors.Take(20)) result.Warn(error);
                int threshold = RimWorldDevBridgeMod.Settings?.RetainedAdapterRestartThreshold ?? 8;
                if (retained >= threshold) result.Warn("Restart RimWorld: retained adapter generation threshold reached.");
            }
            return result;
        }

        internal static void AddAgentContext(BridgeResult result, string packageId)
        {
            if (result == null) return;
            lock (Gate)
            {
                result.Add("adapterIndex", state).Add("adapterFingerprint", fingerprint)
                    .Add("adapterRestartRequired", RestartRequired);
                List<AdapterGeneration> owned = All.Where(item => item.Source != null &&
                    string.Equals(item.Source.PackageId, packageId, StringComparison.OrdinalIgnoreCase))
                    .OrderBy(item => item.Manifest.adapterId, StringComparer.OrdinalIgnoreCase)
                    .ThenByDescending(item => item.BuildUtc).ToList();
                result.Add("selectedAdapterCount", owned.Count(item => item.Selected && item.Compatible))
                    .Add("supersededAdapterCount", owned.Count(item => !item.Selected && item.Compatible))
                    .Add("conflictingAdapterCount", owned.Count(item => item.State == "quarantined-conflict"))
                    .Add("quarantinedAdapterCount", owned.Count(item => item.State.StartsWith("quarantined",
                        StringComparison.OrdinalIgnoreCase)))
                    .Add("incompatibleAdapterCount", owned.Count(item => !item.Compatible));
                foreach (AdapterGeneration item in owned.Take(32))
                {
                    result.AddLine("adapter=id:" + BridgeText.Clean(item.Manifest.adapterId) +
                        " owner:" + BridgeText.Clean(item.Source.PackageId) + " generation:" +
                        BridgeText.Clean(item.Manifest.generation) + " sourceKind:" + item.Source.SourceKind +
                        " sourceIdentity:" + BridgeText.Clean(item.Source.DisplayIdentity) +
                        " fingerprint:" + BridgeAdapterGenerationStore.FingerprintFor(item.Manifest) + " state:" + item.State +
                        " selected:" + item.Selected + " compatible:" + item.Compatible +
                        " health:" + BridgeText.Clean(item.Reason ?? item.LastFailure ?? "none") +
                        " contract:" + (string.Equals(item.Manifest.executionContract, "cooperative-v1",
                            StringComparison.OrdinalIgnoreCase) ? "cooperative-v1" : "legacy-sync-non-cooperative"));
                }
                if (owned.Count > 32) result.Warn("adapter context was bounded to 32 generations");
            }
        }

        internal static void IndexSynchronouslyForTests(IEnumerable<string> packageIds)
        {
            string legacyRoot = Path.GetFullPath(BridgePaths.AdapterPath);
            IndexSynchronouslyForTests(packageIds, new[]
            {
                new BridgeAdapterSourceRecord(BridgeAdapterSourceKind.LegacyDevelopment,
                    "Lan.RimWorldDevBridge", "legacy", legacyRoot, "legacy:DevTools/HotAdapters",
                    Interlocked.Increment(ref sourceGeneration), Array.Empty<BridgeLoadedModuleRecord>())
            });
        }

        internal static void IndexSynchronouslyForTests(IEnumerable<string> packageIds,
            IEnumerable<BridgeAdapterSourceRecord> sources)
        {
            loadedPackages = new HashSet<string>(packageIds ?? Enumerable.Empty<string>(),
                StringComparer.OrdinalIgnoreCase);
            lock (Gate)
            {
                if (indexing) throw new InvalidOperationException("Adapter indexing is already running.");
                indexing = true;
                state = "indexing";
            }
            IndexContext context = new IndexContext(Interlocked.Increment(ref indexGeneration),
                (sources ?? Enumerable.Empty<BridgeAdapterSourceRecord>()).ToList(), loadedPackages);
            IndexWorker(context, false, 0);
        }

        internal static void IndexAsynchronouslyForTests(IEnumerable<string> packageIds,
            IEnumerable<BridgeAdapterSourceRecord> sources, int delayMs)
        {
            loadedPackages = new HashSet<string>(packageIds ?? Enumerable.Empty<string>(),
                StringComparer.OrdinalIgnoreCase);
            lock (Gate)
            {
                indexing = true;
                state = "indexing";
            }
            IndexContext context = new IndexContext(Interlocked.Increment(ref indexGeneration),
                (sources ?? Enumerable.Empty<BridgeAdapterSourceRecord>()).ToList(), loadedPackages);
            ThreadPool.QueueUserWorkItem(_ => IndexWorker(context, false, Math.Max(0, delayMs)));
        }

        private static void IndexWorker(IndexContext context, bool refreshStatus, int testDelayMs)
        {
            Volatile.Write(ref lastIndexThreadId, Thread.CurrentThread.ManagedThreadId);
            long indexStart = Stopwatch.GetTimestamp();
            List<AdapterGeneration> indexed = new List<AdapterGeneration>();
            List<string> errors = new List<string>();
            int ignored = 0;
            try
            {
                if (testDelayMs > 0) Thread.Sleep(testDelayMs);
                foreach (BridgeAdapterSourceRecord source in context.Sources)
                {
                    if (!Directory.Exists(source.DirectoryPath)) continue;
                    DirectoryInfo sourceInfo = new DirectoryInfo(source.DirectoryPath);
                    if ((sourceInfo.Attributes & FileAttributes.ReparsePoint) != 0)
                    {
                        errors.Add(source.DisplayIdentity + ": source directory is a reparse point");
                        continue;
                    }
                    string[] manifests = Directory.GetFiles(source.DirectoryPath, "*.manifest.json",
                        SearchOption.TopDirectoryOnly).OrderBy(value => value, StringComparer.OrdinalIgnoreCase).ToArray();
                    if (manifests.Length > MaximumManifestsPerSource)
                    {
                        errors.Add(source.DisplayIdentity + ": manifest limit exceeded");
                        manifests = manifests.Take(MaximumManifestsPerSource).ToArray();
                    }
                    foreach (string path in manifests)
                    {
                        try
                        {
                            AdapterManifest manifest = BridgeAdapterManifestValidation.Read(path);
                            AdapterGeneration generation = BridgeAdapterManifestValidation.Validate(manifest, path,
                                source, context);
                            indexed.Add(generation);
                        }
                        catch (Exception exception)
                        {
                            errors.Add(source.DisplayIdentity + "/" + Path.GetFileName(path) + ": " +
                                exception.GetBaseException().Message);
                        }
                    }
                    string[] dlls = Directory.GetFiles(source.DirectoryPath, "*.dll", SearchOption.TopDirectoryOnly);
                    HashSet<string> published = new HashSet<string>(indexed.Where(item => item.Source == source)
                        .Select(item => Path.GetFileName(item.AssemblyPath)), StringComparer.OrdinalIgnoreCase);
                    ignored += dlls.Count(path => !published.Contains(Path.GetFileName(path)));
                }
                BridgeAdapterGenerationStore.ResolveDuplicates(indexed, errors);
                BridgeAdapterGenerationStore.SelectActive(indexed, errors);
            }
            catch (Exception exception)
            {
                errors.Add("scan: " + exception.GetBaseException().Message);
            }
            lock (Gate)
            {
                if (context.IndexGeneration != indexGeneration)
                    return;
                BridgeAdapterGenerationStore.MergeLoadedGenerations(All, indexed, errors);
                All.Clear();
                All.AddRange(indexed);
                BridgeAdapterGenerationStore.RebuildCommandIndex(All, Active, CommandsByName, errors);
                IndexErrors.Clear();
                IndexErrors.AddRange(errors);
                ignoredAssemblyCount = ignored;
                indexMs = BridgeTiming.Milliseconds(indexStart);
                fingerprint = BridgeAdapterGenerationStore.ComputeFingerprint(indexed);
                state = errors.Count > 0 ? "ready-with-errors" : "ready";
                indexing = false;
            }
            if (refreshStatus) BridgeRuntime.PostToMainThread(BridgeRuntime.RefreshStatus);
        }

        private static BridgeCommandDescriptor DescriptorFor(AdapterGeneration generation, string name)
        {
            AdapterCommandManifest command = generation.Manifest.commands.First(value =>
                value.name.Equals(name, StringComparison.OrdinalIgnoreCase));
            Enum.TryParse(command.mode, true, out BridgeCommandMode mode);
            Enum.TryParse(command.cost, true, out BridgeCostClass cost);
            return new BridgeCommandDescriptor
            {
                Name = command.name,
                Description = command.description ?? string.Empty,
                Provider = generation.Manifest.adapterId,
                ProviderVersion = generation.Manifest.version,
                Mode = mode,
                Cost = cost,
                RequiresMap = command.requiresMap,
                ArgumentSchema = command.argumentSchema ?? "legacy:string",
                ResultSchema = command.resultSchema ?? "legacy:lines",
                SchemaVersion = command.schemaVersion <= 0 ? 1 : command.schemaVersion,
                MinimumExecutionBudgetMs = command.minimumExecutionBudgetMs <= 0 ? 25 : command.minimumExecutionBudgetMs,
                Cooperative = string.Equals(generation.Manifest.executionContract, "cooperative-v1",
                    StringComparison.OrdinalIgnoreCase),
                NonCooperative = !string.Equals(generation.Manifest.executionContract, "cooperative-v1",
                    StringComparison.OrdinalIgnoreCase)
            };
        }

        private static void Pin(BridgeRequest request, AdapterGeneration generation)
        {
            request.PreparedAdapterId = generation.Manifest.adapterId;
            request.PreparedAdapterGeneration = generation.Manifest.generation;
            request.PreparedAdapter = generation;
        }

        private static BridgeResult FailGeneration(AdapterGeneration generation, BridgeStatus status, string code,
            string detail = null)
        {
            lock (Gate)
            {
                generation.State = "failed";
                generation.Reason = detail ?? code;
                generation.LastFailure = generation.Reason;
            }
            return BridgeResult.Fail(status, code, detail);
        }

        internal sealed class AdapterGeneration
        {
            internal readonly object LoadGate = new object();
            internal AdapterManifest Manifest;
            internal string ManifestPath;
            internal BridgeAdapterSourceRecord Source;
            internal string AssemblyPath;
            internal DateTime BuildUtc;
            internal bool Compatible;
            internal bool Selected;
            internal string State;
            internal string Reason;
            internal byte[] PreparedBytes;
            internal Assembly PreparedAssembly;
            internal Assembly Assembly;
            internal IBridgeAdapterProvider TypedProvider;
            internal MethodInfo LegacyExecute;
            internal long InvocationCount;
            internal long FailureCount;
            internal int ConsecutiveFailures;
            internal double TotalExecutionMs;
            internal double LastExecutionMs;
            internal double LoadMs;
            internal BridgeStatus LastStatus;
            internal string LastFailure;
            internal string Verification;
            internal DateTime QuarantinedUntilUtc;
            internal bool RetainedOnly;
            internal long SeriousOverruns;

            internal void CopyRuntime(AdapterGeneration previous)
            {
                PreparedBytes = previous.PreparedBytes;
                PreparedAssembly = previous.PreparedAssembly;
                Assembly = previous.Assembly;
                TypedProvider = previous.TypedProvider;
                LegacyExecute = previous.LegacyExecute;
                InvocationCount = previous.InvocationCount;
                FailureCount = previous.FailureCount;
                ConsecutiveFailures = previous.ConsecutiveFailures;
                TotalExecutionMs = previous.TotalExecutionMs;
                LastExecutionMs = previous.LastExecutionMs;
                LoadMs = previous.LoadMs;
                LastStatus = previous.LastStatus;
                LastFailure = previous.LastFailure;
                Verification = previous.Verification;
                QuarantinedUntilUtc = previous.QuarantinedUntilUtc;
                SeriousOverruns = previous.SeriousOverruns;
                if (Assembly != null) State = "loaded";
            }
        }
    }

    internal enum BridgeAdapterSourceKind
    {
        OwnerMod = 0,
        LegacyDevelopment = 1
    }

    internal sealed class BridgeLoadedModuleRecord
    {
        internal readonly string PackageId;
        internal readonly string RelativePath;
        internal readonly string FullPath;
        internal readonly string AssemblyIdentity;
        internal readonly Guid ModuleMvid;
        internal readonly long Length;

        internal BridgeLoadedModuleRecord(string packageId, string relativePath, string fullPath,
            string assemblyIdentity, Guid moduleMvid, long length)
        {
            PackageId = packageId;
            RelativePath = relativePath;
            FullPath = fullPath;
            AssemblyIdentity = assemblyIdentity;
            ModuleMvid = moduleMvid;
            Length = length;
        }
    }

    internal sealed class BridgeAdapterSourceRecord
    {
        internal readonly BridgeAdapterSourceKind SourceKind;
        internal readonly string PackageId;
        internal readonly string LoadedVersion;
        internal readonly string OwnerRootPath;
        internal readonly string DirectoryPath;
        internal readonly string DisplayIdentity;
        internal readonly long SourceGeneration;
        internal readonly IReadOnlyList<BridgeLoadedModuleRecord> LoadedModules;

        internal BridgeAdapterSourceRecord(BridgeAdapterSourceKind sourceKind, string packageId,
            string loadedVersion, string ownerRootPath, string displayIdentity, long sourceGeneration,
            IReadOnlyList<BridgeLoadedModuleRecord> loadedModules)
        {
            SourceKind = sourceKind;
            PackageId = packageId;
            LoadedVersion = loadedVersion;
            OwnerRootPath = Path.GetFullPath(ownerRootPath);
            DirectoryPath = sourceKind == BridgeAdapterSourceKind.OwnerMod
                ? Path.Combine(OwnerRootPath, "DevTools", "BridgeAdapters") : OwnerRootPath;
            DisplayIdentity = displayIdentity;
            SourceGeneration = sourceGeneration;
            LoadedModules = (loadedModules ?? Array.Empty<BridgeLoadedModuleRecord>()).ToList().AsReadOnly();
        }
    }

    internal sealed class IndexContext
    {
        internal readonly long IndexGeneration;
        internal readonly IReadOnlyList<BridgeAdapterSourceRecord> Sources;
        internal readonly IReadOnlyCollection<string> LoadedPackages;

        internal IndexContext(long indexGeneration, IReadOnlyList<BridgeAdapterSourceRecord> sources,
            IReadOnlyCollection<string> loadedPackages)
        {
            IndexGeneration = indexGeneration;
            Sources = (sources ?? Array.Empty<BridgeAdapterSourceRecord>()).ToList().AsReadOnly();
            LoadedPackages = new HashSet<string>(loadedPackages ?? Array.Empty<string>(),
                StringComparer.OrdinalIgnoreCase);
        }
    }

    [DataContract]
    internal sealed class AdapterManifest
    {
        [DataMember(Order = 1)] public int manifestVersion;
        [DataMember(Order = 2)] public string adapterId;
        [DataMember(Order = 3)] public string displayName;
        [DataMember(Order = 4)] public string version;
        [DataMember(Order = 5)] public string generation;
        [DataMember(Order = 6)] public string buildUtc;
        [DataMember(Order = 7)] public string assemblyFile;
        [DataMember(Order = 8)] public string assemblyIdentity;
        [DataMember(Order = 9)] public long assemblyBytes;
        [DataMember(Order = 10)] public string contentHash;
        [DataMember(Order = 11)] public string providerType;
        [DataMember(Order = 12)] public int protocolMin;
        [DataMember(Order = 13)] public int protocolMax;
        [DataMember(Order = 14)] public List<AdapterCommandManifest> commands;
        [DataMember(Order = 15)] public List<string> requiredPackageIds;
        [DataMember(Order = 16)] public List<string> optionalPackageIds;
        [DataMember(Order = 17)] public string changeSummary;
        [DataMember(Order = 18)] public string assemblySource;
        [DataMember(Order = 19)] public string modulePackageId;
        [DataMember(Order = 20)] public string moduleRelativePath;
        [DataMember(Order = 21)] public string moduleMvid;
        [DataMember(Order = 22)] public string executionContract;
    }

    [DataContract]
    internal sealed class AdapterCommandManifest
    {
        [DataMember(Order = 1)] public string name;
        [DataMember(Order = 2)] public string description;
        [DataMember(Order = 3)] public string mode;
        [DataMember(Order = 4)] public string cost;
        [DataMember(Order = 5)] public bool requiresMap;
        [DataMember(Order = 6)] public string argumentSchema;
        [DataMember(Order = 7)] public string resultSchema;
        [DataMember(Order = 8)] public int schemaVersion;
        [DataMember(Order = 9)] public int minimumExecutionBudgetMs;
        [DataMember(Order = 10)] public string providerCommand;
    }
}
