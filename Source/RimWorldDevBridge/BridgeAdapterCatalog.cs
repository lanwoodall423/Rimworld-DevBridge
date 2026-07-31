using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Linq;
using System.Reflection;
using System.Runtime.Serialization;
using System.Runtime.Serialization.Json;
using System.Security.Cryptography;
using System.Text;
using System.Threading;
using Verse;

namespace RimWorldDevBridge
{
    internal static class BridgeAdapterCatalog
    {
        private const int CircuitBreakFailures = 3;
        private static readonly object Gate = new object();
        private static readonly Dictionary<string, AdapterGeneration> Active =
            new Dictionary<string, AdapterGeneration>(StringComparer.OrdinalIgnoreCase);
        private static readonly Dictionary<string, AdapterGeneration> CommandsByName =
            new Dictionary<string, AdapterGeneration>(StringComparer.OrdinalIgnoreCase);
        private static readonly List<AdapterGeneration> All = new List<AdapterGeneration>();
        private static readonly List<string> IndexErrors = new List<string>();
        private static HashSet<string> loadedPackages = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        private static Dictionary<string, string> loadedPackageRoots =
            new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
        private static volatile bool indexing;
        private static string state = "dormant";
        private static string fingerprint = BridgeProtocol.CoreSchema;
        private static int ignoredAssemblyCount;
        private static double indexMs;

        internal static bool Indexing => indexing;
        internal static string State => state;
        internal static string Fingerprint => fingerprint;
        internal static IEnumerable<BridgeCommandDescriptor> Commands
        {
            get { lock (Gate) return CommandsByName.Keys.OrderBy(value => value)
                .Select(value => DescriptorFor(CommandsByName[value], value)).ToList(); }
        }

        internal static void ActivateIndexing()
        {
            loadedPackages = new HashSet<string>(LoadedModManager.RunningModsListForReading
                .Select(mod => mod.PackageIdPlayerFacing), StringComparer.OrdinalIgnoreCase);
            loadedPackageRoots = LoadedModManager.RunningModsListForReading
                .Where(mod => !string.IsNullOrWhiteSpace(mod.PackageIdPlayerFacing) &&
                    !string.IsNullOrWhiteSpace(mod.RootDir))
                .GroupBy(mod => mod.PackageIdPlayerFacing, StringComparer.OrdinalIgnoreCase)
                .ToDictionary(group => group.Key, group => Path.GetFullPath(group.First().RootDir),
                    StringComparer.OrdinalIgnoreCase);
            BeginIndex();
        }

        internal static BridgeResult Reindex()
        {
            bool started = BeginIndex();
            return BridgeResult.Ok("core.adapterReindex")
                .Add("started", started)
                .Add("state", State)
                .Add("note", "Manifest parsing and hashing run off the game thread.");
        }

        private static bool BeginIndex()
        {
            lock (Gate)
            {
                if (indexing) return false;
                indexing = true;
                state = "indexing";
            }
            ThreadPool.QueueUserWorkItem(_ => IndexWorker(true));
            return true;
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
                    preparedAssembly = AppDomain.CurrentDomain.GetAssemblies().FirstOrDefault(item =>
                        string.Equals(item.FullName, generation.Manifest.assemblyIdentity, StringComparison.Ordinal));
                    if (preparedAssembly == null)
                        return BridgeResult.Fail(BridgeStatus.UNAVAILABLE, "loaded_adapter_assembly_missing",
                            generation.Manifest.assemblyIdentity);
                    if (!loadedPackageRoots.TryGetValue(generation.Manifest.modulePackageId,
                        out string packageRoot))
                        return BridgeResult.Fail(BridgeStatus.UNAVAILABLE, "loaded_adapter_package_missing",
                            generation.Manifest.modulePackageId);
                    string root = Path.GetFullPath(packageRoot) + Path.DirectorySeparatorChar;
                    assemblyPath = Path.GetFullPath(Path.Combine(root, generation.Manifest.moduleRelativePath));
                    if (!assemblyPath.StartsWith(root, StringComparison.OrdinalIgnoreCase) || !File.Exists(assemblyPath))
                        return BridgeResult.Fail(BridgeStatus.INCOMPATIBLE, "loaded_adapter_module_missing");
                }
                byte[] bytes = null;
                if (!string.IsNullOrWhiteSpace(assemblyPath))
                {
                    bytes = ReadAllBytesShared(assemblyPath);
                    if (bytes.LongLength != generation.Manifest.assemblyBytes)
                        return FailGeneration(generation, BridgeStatus.INCOMPATIBLE,
                            "adapter_size_mismatch");
                    string hash = Hash(bytes);
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
        {
            AdapterGeneration generation;
            lock (Gate)
            {
                generation = context.Request.PreparedAdapter as AdapterGeneration;
                if (generation == null) generation = All.FirstOrDefault(item =>
                    string.Equals(item.Manifest.adapterId, context.Request.PreparedAdapterId,
                        StringComparison.OrdinalIgnoreCase) &&
                    string.Equals(item.Manifest.generation, context.Request.PreparedAdapterGeneration,
                        StringComparison.OrdinalIgnoreCase));
                if (generation == null && !CommandsByName.TryGetValue(context.Request.Command, out generation)) return null;
                if (generation.QuarantinedUntilUtc > DateTime.UtcNow)
                    return BridgeResult.Fail(BridgeStatus.UNAVAILABLE, "adapter_circuit_open");
            }
            try
            {
                EnsureLoaded(generation);
                long start = Stopwatch.GetTimestamp();
                BridgeResult result;
                if (generation.TypedProvider != null)
                {
                    result = generation.TypedProvider.Execute(context);
                }
                else
                {
                    AdapterCommandManifest command = generation.Manifest.commands.First(item =>
                        item.name.Equals(context.Request.Command, StringComparison.OrdinalIgnoreCase));
                    object value = generation.LegacyExecute.Invoke(null,
                        new object[] { command.providerCommand ?? command.name,
                            context.Request.Argument ?? string.Empty, context.Map });
                    result = BridgeResult.FromLegacy(value as IEnumerable<string>);
                    if (context.Request.Mode != BridgeCommandMode.PureRead &&
                        string.Equals(result.MutationSummary, "none", StringComparison.Ordinal))
                        result.MutationSummary = "legacy adapter command completed; no detailed mutation summary supplied";
                }
                double elapsed = BridgeTiming.Milliseconds(start);
                lock (Gate)
                {
                    generation.InvocationCount++;
                    generation.TotalExecutionMs += elapsed;
                    generation.LastExecutionMs = elapsed;
                    generation.LastStatus = result.Status;
                    if (result.IsSuccess)
                    {
                        generation.ConsecutiveFailures = 0;
                        generation.LastFailure = null;
                        generation.State = "loaded";
                    }
                    else
                    {
                        generation.FailureCount++;
                        generation.ConsecutiveFailures++;
                        generation.LastFailure = result.Status.ToString();
                        if (generation.ConsecutiveFailures >= CircuitBreakFailures)
                        {
                            generation.State = "quarantined";
                            generation.QuarantinedUntilUtc = DateTime.UtcNow.AddMinutes(2);
                        }
                    }
                }
                if (elapsed >= 50d) result.Warn("slow adapter command: " + elapsed.ToString("0.###") + " ms");
                return result;
            }
            catch (Exception exception)
            {
                Exception root = exception.GetBaseException();
                lock (Gate)
                {
                    generation.InvocationCount++;
                    generation.FailureCount++;
                    generation.ConsecutiveFailures++;
                    generation.LastStatus = BridgeStatus.ERROR;
                    generation.LastFailure = root.GetType().Name + ": " + root.Message;
                    if (generation.ConsecutiveFailures >= CircuitBreakFailures)
                    {
                        generation.State = "quarantined";
                        generation.QuarantinedUntilUtc = DateTime.UtcNow.AddMinutes(2);
                    }
                }
                return BridgeResult.Fail(BridgeStatus.ERROR, "adapter_failure", root.Message);
            }
        }

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
                        BridgeText.Clean(item.Verification ?? "manifest-only"));
                }
                foreach (string error in IndexErrors.Take(20)) result.Warn(error);
                int threshold = RimWorldDevBridgeMod.Settings?.RetainedAdapterRestartThreshold ?? 8;
                if (retained >= threshold) result.Warn("Restart RimWorld: retained adapter generation threshold reached.");
            }
            return result;
        }

        internal static void IndexSynchronouslyForTests(IEnumerable<string> packageIds)
        {
            loadedPackages = new HashSet<string>(packageIds ?? Enumerable.Empty<string>(),
                StringComparer.OrdinalIgnoreCase);
            lock (Gate)
            {
                if (indexing) throw new InvalidOperationException("Adapter indexing is already running.");
                indexing = true;
                state = "indexing";
            }
            IndexWorker(false);
        }

        private static void IndexWorker(bool refreshStatus)
        {
            long indexStart = Stopwatch.GetTimestamp();
            List<AdapterGeneration> indexed = new List<AdapterGeneration>();
            List<string> errors = new List<string>();
            int ignored = 0;
            try
            {
                Directory.CreateDirectory(BridgePaths.AdapterPath);
                foreach (string path in Directory.GetFiles(BridgePaths.AdapterPath, "*.manifest.json")
                    .OrderBy(value => value, StringComparer.OrdinalIgnoreCase))
                {
                    try
                    {
                        AdapterManifest manifest = ReadManifest(path);
                        AdapterGeneration generation = Validate(manifest, path);
                        indexed.Add(generation);
                    }
                    catch (Exception exception)
                    {
                        errors.Add(Path.GetFileName(path) + ": " + exception.GetBaseException().Message);
                    }
                }
                SelectActive(indexed, errors);
                HashSet<string> published = new HashSet<string>(indexed.Select(item =>
                    Path.GetFileName(item.AssemblyPath)), StringComparer.OrdinalIgnoreCase);
                ignored = Directory.GetFiles(BridgePaths.AdapterPath, "*.dll")
                    .Count(path => !published.Contains(Path.GetFileName(path)));
            }
            catch (Exception exception)
            {
                errors.Add("scan: " + exception.GetBaseException().Message);
            }
            lock (Gate)
            {
                MergeLoadedGenerations(indexed);
                All.Clear();
                All.AddRange(indexed);
                RebuildCommandIndex(errors);
                IndexErrors.Clear();
                IndexErrors.AddRange(errors);
                ignoredAssemblyCount = ignored;
                indexMs = BridgeTiming.Milliseconds(indexStart);
                fingerprint = ComputeFingerprint(indexed);
                state = errors.Count > 0 ? "ready-with-errors" : "ready";
                indexing = false;
            }
            if (refreshStatus) BridgeRuntime.RefreshStatus();
        }

        private static AdapterManifest ReadManifest(string path)
        {
            FileInfo info = new FileInfo(path);
            if (info.Length <= 0 || info.Length > 1024 * 1024)
                throw new InvalidDataException("Manifest size is invalid.");
            using (FileStream stream = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.Read))
                return (AdapterManifest)new DataContractJsonSerializer(typeof(AdapterManifest)).ReadObject(stream);
        }

        private static AdapterGeneration Validate(AdapterManifest manifest, string manifestPath)
        {
            if (manifest == null) throw new InvalidDataException("Manifest is empty.");
            if (manifest.manifestVersion != 1) throw new InvalidDataException("Unsupported manifestVersion.");
            RequireName(manifest.adapterId, "adapterId");
            if (string.IsNullOrWhiteSpace(manifest.displayName)) throw new InvalidDataException("displayName is required.");
            if (string.IsNullOrWhiteSpace(manifest.version)) throw new InvalidDataException("version is required.");
            if (string.IsNullOrWhiteSpace(manifest.generation)) throw new InvalidDataException("generation is required.");
            if (!DateTime.TryParse(manifest.buildUtc, null,
                System.Globalization.DateTimeStyles.AdjustToUniversal, out DateTime buildUtc))
                throw new InvalidDataException("buildUtc is invalid.");
            if (manifest.protocolMin > BridgeProtocol.ProtocolVersion || manifest.protocolMax < BridgeProtocol.ProtocolVersion)
                return NewGeneration(manifest, manifestPath, buildUtc, false, "protocol incompatible");
            if (string.IsNullOrWhiteSpace(manifest.providerType)) throw new InvalidDataException("providerType is required.");
            bool loadedAssembly = string.Equals(manifest.assemblySource, "loaded", StringComparison.OrdinalIgnoreCase);
            if (!loadedAssembly && !string.IsNullOrEmpty(manifest.assemblySource) &&
                !string.Equals(manifest.assemblySource, "file", StringComparison.OrdinalIgnoreCase))
                throw new InvalidDataException("assemblySource must be file or loaded.");
            manifest.assemblySource = loadedAssembly ? "loaded" : "file";
            if (!loadedAssembly && string.IsNullOrWhiteSpace(manifest.assemblyFile))
                throw new InvalidDataException("assemblyFile is required.");
            if (loadedAssembly && string.IsNullOrWhiteSpace(manifest.assemblyIdentity))
                throw new InvalidDataException("assemblyIdentity is required for a loaded adapter.");
            if (loadedAssembly && (string.IsNullOrWhiteSpace(manifest.modulePackageId) ||
                string.IsNullOrWhiteSpace(manifest.moduleRelativePath) ||
                Path.IsPathRooted(manifest.moduleRelativePath) || manifest.moduleRelativePath.Split(
                    new[] { Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar },
                    StringSplitOptions.RemoveEmptyEntries).Any(part => part == "..")))
                throw new InvalidDataException("Loaded adapter module package/path is invalid.");
            if (loadedAssembly && !(manifest.requiredPackageIds ?? new List<string>()).Any(package =>
                string.Equals(package, manifest.modulePackageId, StringComparison.OrdinalIgnoreCase)))
                throw new InvalidDataException("Loaded adapter module package must be required.");
            string directory = Path.GetDirectoryName(manifestPath);
            string assemblyPath = null;
            if (!loadedAssembly)
            {
                assemblyPath = Path.GetFullPath(Path.Combine(directory, manifest.assemblyFile));
                if (!assemblyPath.StartsWith(Path.GetFullPath(BridgePaths.AdapterPath) + Path.DirectorySeparatorChar,
                    StringComparison.OrdinalIgnoreCase)) throw new InvalidDataException("assemblyFile escaped adapter directory.");
                if (manifest.assemblyFile.EndsWith(".tmp", StringComparison.OrdinalIgnoreCase) || !File.Exists(assemblyPath))
                    throw new InvalidDataException("assemblyFile is missing or partial.");
                long actualBytes = new FileInfo(assemblyPath).Length;
                if (manifest.assemblyBytes <= 0 || manifest.assemblyBytes != actualBytes)
                    throw new InvalidDataException("assemblyBytes does not match the published file.");
            }
            else if (manifest.assemblyBytes <= 0)
                throw new InvalidDataException("assemblyBytes is required.");
            if (string.IsNullOrWhiteSpace(manifest.contentHash) || manifest.contentHash.Length != 64)
                throw new InvalidDataException("contentHash must be SHA-256 hex.");
            if (manifest.commands == null || manifest.commands.Count == 0)
                throw new InvalidDataException("commands are required.");
            HashSet<string> names = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
            foreach (AdapterCommandManifest command in manifest.commands)
            {
                RequireName(command.name, "command name");
                command.name = BridgeText.NormalizeCommand(command.name);
                command.providerCommand = BridgeText.NormalizeCommand(command.providerCommand ?? command.name);
                RequireName(command.providerCommand, "provider command name");
                if (!names.Add(command.name)) throw new InvalidDataException("Duplicate command " + command.name + ".");
                if (string.Equals(command.mode, "R", StringComparison.OrdinalIgnoreCase))
                    command.mode = BridgeCommandMode.PureRead.ToString();
                else if (string.Equals(command.mode, "W", StringComparison.OrdinalIgnoreCase))
                    command.mode = BridgeCommandMode.PersistentMutation.ToString();
                else if (!Enum.TryParse(command.mode, true, out BridgeCommandMode parsedMode))
                    throw new InvalidDataException("Unknown command mode for " + command.name + ".");
                else command.mode = parsedMode.ToString();
                if (!Enum.TryParse(command.cost, true, out BridgeCostClass parsedCost))
                    throw new InvalidDataException("Unknown command cost for " + command.name + ".");
                command.cost = parsedCost.ToString();
            }
            string missing = (manifest.requiredPackageIds ?? new List<string>())
                .FirstOrDefault(package => !loadedPackages.Contains(package));
            return NewGeneration(manifest, manifestPath, buildUtc, missing == null,
                missing == null ? null : "missing package " + missing);
        }

        private static AdapterGeneration NewGeneration(AdapterManifest manifest, string manifestPath,
            DateTime buildUtc, bool compatible, string reason)
        {
            return new AdapterGeneration
            {
                Manifest = manifest,
                ManifestPath = manifestPath,
                AssemblyPath = string.Equals(manifest.assemblySource, "loaded", StringComparison.OrdinalIgnoreCase)
                    ? null : Path.GetFullPath(Path.Combine(Path.GetDirectoryName(manifestPath), manifest.assemblyFile)),
                BuildUtc = buildUtc,
                Compatible = compatible,
                Reason = reason,
                State = compatible ? "available" : "incompatible"
            };
        }

        private static void SelectActive(List<AdapterGeneration> indexed, List<string> errors)
        {
            foreach (IGrouping<string, AdapterGeneration> group in indexed.GroupBy(item => item.Manifest.adapterId,
                StringComparer.OrdinalIgnoreCase))
            {
                AdapterGeneration selected = group.Where(item => item.Compatible)
                    .OrderByDescending(item => item.BuildUtc).ThenByDescending(item => item.Manifest.generation,
                        StringComparer.OrdinalIgnoreCase).FirstOrDefault();
                foreach (AdapterGeneration item in group)
                {
                    item.Selected = item == selected;
                    if (item == selected) item.State = "available";
                    else if (item.Compatible) { item.State = "superseded"; item.Reason = "newer generation selected"; }
                }
                if (selected == null) errors.Add(group.Key + ": no compatible generation");
            }
        }

        private static void MergeLoadedGenerations(List<AdapterGeneration> indexed)
        {
            foreach (AdapterGeneration previous in All.Where(item => item.Assembly != null))
            {
                AdapterGeneration current = indexed.FirstOrDefault(item =>
                    item.Manifest.adapterId.Equals(previous.Manifest.adapterId, StringComparison.OrdinalIgnoreCase) &&
                    item.Manifest.generation.Equals(previous.Manifest.generation, StringComparison.OrdinalIgnoreCase));
                if (current != null)
                {
                    current.CopyRuntime(previous);
                    if (!current.Selected) current.State = "retained-superseded";
                }
                else
                {
                    previous.State = "retained-superseded";
                    indexed.Add(previous);
                }
            }
        }

        private static void RebuildCommandIndex(List<string> errors)
        {
            Active.Clear();
            CommandsByName.Clear();
            foreach (AdapterGeneration generation in All.Where(item => item.Selected && item.Compatible &&
                item.State != "failed" && item.State != "quarantined")
                .OrderBy(item => item.Manifest.adapterId, StringComparer.OrdinalIgnoreCase))
            {
                Active[generation.Manifest.adapterId] = generation;
                bool collision = false;
                foreach (AdapterCommandManifest command in generation.Manifest.commands)
                {
                    if (BridgeCommands.Describe(command.name) != null || CommandsByName.ContainsKey(command.name))
                    {
                        collision = true;
                        errors.Add(generation.Manifest.adapterId + ": command collision " + command.name);
                        break;
                    }
                }
                if (collision)
                {
                    generation.State = "quarantined";
                    generation.Reason = "command collision";
                    Active.Remove(generation.Manifest.adapterId);
                    continue;
                }
                foreach (AdapterCommandManifest command in generation.Manifest.commands)
                    CommandsByName[command.name] = generation;
            }
        }

        private static void EnsureLoaded(AdapterGeneration generation)
        {
            if (generation.Assembly != null) return;
            lock (generation.LoadGate)
            {
                if (generation.Assembly != null) return;
                byte[] bytes = generation.PreparedBytes;
                Assembly preparedAssembly = generation.PreparedAssembly;
                if (bytes == null && preparedAssembly == null)
                    throw new InvalidOperationException("Adapter was not prepared off-thread.");
                long loadStart = Stopwatch.GetTimestamp();
                Assembly assembly = preparedAssembly ?? Assembly.Load(bytes);
                if (!string.IsNullOrWhiteSpace(generation.Manifest.assemblyIdentity) &&
                    !assembly.FullName.Equals(generation.Manifest.assemblyIdentity, StringComparison.Ordinal))
                    throw new InvalidDataException("Adapter assembly identity does not match its manifest.");
                Type providerType = assembly.GetType(generation.Manifest.providerType, true, false);
                if (typeof(IBridgeAdapterProvider).IsAssignableFrom(providerType))
                {
                    generation.TypedProvider = (IBridgeAdapterProvider)Activator.CreateInstance(providerType);
                    ValidateTypedProvider(generation);
                }
                else
                {
                    generation.LegacyExecute = providerType.GetMethod("ExecuteBridgeCommand",
                        BindingFlags.Public | BindingFlags.Static, null,
                        new[] { typeof(string), typeof(string), typeof(Map) }, null);
                    if (generation.LegacyExecute == null)
                        throw new MissingMethodException(providerType.FullName, "ExecuteBridgeCommand");
                }
                generation.Assembly = assembly;
                generation.PreparedBytes = null;
                generation.PreparedAssembly = null;
                generation.LoadMs = BridgeTiming.Milliseconds(loadStart);
                generation.State = "loaded";
            }
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
                MinimumExecutionBudgetMs = command.minimumExecutionBudgetMs <= 0 ? 25 : command.minimumExecutionBudgetMs
            };
        }

        private static void Pin(BridgeRequest request, AdapterGeneration generation)
        {
            request.PreparedAdapterId = generation.Manifest.adapterId;
            request.PreparedAdapterGeneration = generation.Manifest.generation;
            request.PreparedAdapter = generation;
        }

        private static void ValidateTypedProvider(AdapterGeneration generation)
        {
            BridgeAdapterMetadata metadata = generation.TypedProvider.Metadata ??
                throw new InvalidDataException("Typed adapter metadata is missing.");
            if (!string.Equals(metadata.Id, generation.Manifest.adapterId, StringComparison.OrdinalIgnoreCase) ||
                !string.Equals(metadata.Version, generation.Manifest.version, StringComparison.OrdinalIgnoreCase))
                throw new InvalidDataException("Typed adapter metadata does not match its manifest.");
            Dictionary<string, BridgeCommandDescriptor> declared = (generation.TypedProvider.Commands ??
                Enumerable.Empty<BridgeCommandDescriptor>()).Where(item => item != null)
                .ToDictionary(item => BridgeText.NormalizeCommand(item.Name), StringComparer.OrdinalIgnoreCase);
            foreach (AdapterCommandManifest command in generation.Manifest.commands)
            {
                if (!declared.TryGetValue(command.name, out BridgeCommandDescriptor descriptor))
                    throw new InvalidDataException("Typed provider did not declare " + command.name + ".");
                Enum.TryParse(command.mode, true, out BridgeCommandMode mode);
                if (descriptor.Mode != mode)
                    throw new InvalidDataException("Typed provider mode differs for " + command.name + ".");
            }
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

        private static string ComputeFingerprint(IEnumerable<AdapterGeneration> generations)
        {
            string source = BridgeProtocol.CoreSchema + "|" + string.Join("|", generations
                .Where(item => item.State == "available" || item.State == "loaded" || item.State == "prepared")
                .OrderBy(item => item.Manifest.adapterId).Select(item => item.Manifest.adapterId + ":" +
                    item.Manifest.version + ":" + item.Manifest.generation + ":" + item.Manifest.contentHash));
            using (SHA256 algorithm = SHA256.Create())
                return string.Concat(algorithm.ComputeHash(Encoding.UTF8.GetBytes(source)).Take(6)
                    .Select(value => value.ToString("x2")));
        }

        private static string Hash(byte[] bytes)
        {
            using (SHA256 algorithm = SHA256.Create())
                return string.Concat(algorithm.ComputeHash(bytes).Select(value => value.ToString("X2")));
        }

        private static byte[] ReadAllBytesShared(string path)
        {
            using (FileStream stream = new FileStream(path, FileMode.Open, FileAccess.Read,
                FileShare.ReadWrite | FileShare.Delete))
            {
                byte[] bytes = new byte[stream.Length];
                int offset = 0;
                while (offset < bytes.Length)
                {
                    int read = stream.Read(bytes, offset, bytes.Length - offset);
                    if (read == 0) throw new EndOfStreamException("Adapter changed while being read.");
                    offset += read;
                }
                return bytes;
            }
        }

        private static void RequireName(string value, string label)
        {
            if (string.IsNullOrWhiteSpace(value) || value.Any(character =>
                !char.IsLetterOrDigit(character) && character != '_' && character != '-' && character != '.'))
                throw new InvalidDataException(label + " is invalid.");
        }

        private sealed class AdapterGeneration
        {
            internal readonly object LoadGate = new object();
            internal AdapterManifest Manifest;
            internal string ManifestPath;
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
                if (Assembly != null) State = "loaded";
            }
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
