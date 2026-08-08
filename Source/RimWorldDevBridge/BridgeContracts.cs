using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Linq;
using System.Threading;
using RimWorld.Planet;
using Verse;

namespace RimWorldDevBridge
{
    public enum BridgeStatus
    {
        OK,
        NOT_FOUND,
        INVALID_ARGUMENT,
        UNAVAILABLE,
        INCOMPATIBLE,
        FORBIDDEN,
        TIMEOUT,
        CANCELLED,
        BUSY,
        PARTIAL,
        BLOCKED,
        ERROR
    }

    public enum BridgeCommandMode
    {
        PureRead = 0,
        UiOnly = 1,
        Reversible = 2,
        TemporaryTestMutation = 3,
        PersistentMutation = 4,
        PotentiallyDestructive = 5
    }

    public enum BridgeCostClass
    {
        Trivial = 0,
        Normal = 1,
        Expensive = 2,
        Simulation = 3
    }

    public sealed class BridgeCommandDescriptor
    {
        public string Name { get; set; }
        public string Description { get; set; }
        public string Provider { get; set; }
        public string ProviderVersion { get; set; }
        public BridgeCommandMode Mode { get; set; }
        public BridgeCostClass Cost { get; set; }
        public bool RequiresMap { get; set; }
        public string ArgumentSchema { get; set; }
        public string ResultSchema { get; set; }
        public int SchemaVersion { get; set; } = 1;
        public int MinimumExecutionBudgetMs { get; set; } = 25;
        public bool Cooperative { get; set; }
        public bool NonCooperative { get; set; }

        public BridgeCommandDescriptor Clone()
        {
            return (BridgeCommandDescriptor)MemberwiseClone();
        }
    }

    [System.Runtime.Serialization.DataContract]
    public sealed class BridgeField
    {
        [System.Runtime.Serialization.DataMember(Order = 1)] public string Name { get; set; }
        [System.Runtime.Serialization.DataMember(Order = 2)] public string Value { get; set; }
        [System.Runtime.Serialization.DataMember(Order = 3)] public string ValueType { get; set; }

        public BridgeField() { }
        public BridgeField(string name, object value)
        {
            Name = BridgeText.Bound(name ?? "value", 256);
            ValueType = value == null ? "null" : value is bool ? "boolean" :
                value is byte || value is sbyte || value is short || value is ushort || value is int ||
                value is uint || value is long || value is ulong ? "integer" :
                value is float || value is double || value is decimal ? "number" : "string";
            Value = BridgeText.Bound(value, 16384);
        }

        internal BridgeField Clone() => new BridgeField(Name, Value) { ValueType = ValueType };

        internal bool EnforceBounds()
        {
            string name = BridgeText.Bound(Name ?? "value", 256);
            string value = BridgeText.Bound(Value, 16384);
            string valueType = BridgeText.Bound(ValueType ?? "string", 32);
            bool changed = name != Name || value != Value || valueType != ValueType;
            Name = name;
            Value = value;
            ValueType = valueType;
            return changed;
        }
    }

    public sealed class BridgeResult
    {
        private const int MaximumFields = 512;
        private const int MaximumLines = 1024;
        private const int MaximumWarnings = 64;
        public string RequestId { get; set; }
        public string CorrelationId { get; set; }
        public string AgentId { get; set; }
        public string ClientInstanceId { get; set; }
        public string ParticipantId { get; set; }
        public string SessionId { get; set; }
        public string ConnectionSessionId { get; set; }
        public string Command { get; set; }
        public string OperationId { get; set; }
        public string OperationKind { get; set; }
        public string OperationState { get; set; }
        public string CompatibilityKey { get; set; }
        public string DesiredState { get; set; }
        public string RuntimeSlotId { get; set; }
        public string DeploymentId { get; set; }
        public string ArtifactFingerprint { get; set; }
        public string LoadedAssemblyFingerprint { get; set; }
        public int ProcessId { get; set; }
        public string ProcessStartIdentity { get; set; }
        public long LifecycleGeneration { get; set; }
        public long ProgressSequence { get; set; }
        public DateTime? LastProgressAtUtc { get; set; }
        public bool Terminal { get; set; }
        public bool Recoverable { get; set; }
        public bool RetrySafe { get; set; }
        public string NextAction { get; set; }
        public string CapacityState { get; set; }
        public bool KeepRunning { get; set; }
        public string Provider { get; set; } = "core";
        public string ProviderVersion { get; set; } = BridgeProtocol.BridgeVersion;
        public BridgeCommandMode Mode { get; set; } = BridgeCommandMode.PureRead;
        public BridgeStatus Status { get; set; } = BridgeStatus.OK;
        public string Schema { get; set; } = "core.result";
        public int SchemaVersion { get; set; } = 1;
        public double QueueDelayMs { get; set; }
        public double PreparationMs { get; set; }
        public double ExecutionMs { get; set; }
        internal int MainThreadBudgetMs { get; set; }
        internal bool MainThreadOverrun { get; set; }
        internal double MaxMainThreadStepMs { get; set; }
        internal int CooperativeSteps { get; set; }
        internal bool NonCooperativeExecution { get; set; }
        public int TickBefore { get; set; } = -1;
        public int TickAfter { get; set; } = -1;
        public bool Truncated { get; set; }
        public string ContinuationCursor { get; set; }
        public string MutationSummary { get; set; } = "none";
        public List<BridgeField> Data { get; } = new List<BridgeField>();
        public List<string> Lines { get; } = new List<string>();
        public List<string> Warnings { get; } = new List<string>();

        public bool IsSuccess => Status == BridgeStatus.OK || Status == BridgeStatus.PARTIAL;

        public BridgeResult Add(string name, object value)
        {
            if (Data.Count >= MaximumFields)
            {
                Truncated = true;
                return this;
            }
            if ((name?.Length ?? 0) > 256 || BridgeText.Invariant(value).Length > 16384)
                Truncated = true;
            Data.Add(new BridgeField(name, value));
            return this;
        }

        public BridgeResult AddLine(string line)
        {
            if (Lines.Count >= MaximumLines)
            {
                Truncated = true;
                return this;
            }
            if (BridgeText.Invariant(line).Length > 16384) Truncated = true;
            Lines.Add(BridgeText.Bound(line, 16384));
            return this;
        }

        public BridgeResult Warn(string warning)
        {
            if (string.IsNullOrWhiteSpace(warning)) return this;
            if (Warnings.Count >= MaximumWarnings)
            {
                Truncated = true;
                return this;
            }
            if (BridgeText.Invariant(warning).Length > 4096) Truncated = true;
            Warnings.Add(BridgeText.Bound(warning, 4096));
            return this;
        }

        internal void AddCopiedField(BridgeField field)
        {
            if (field != null && Data.Count < MaximumFields) Data.Add(field.Clone());
        }

        internal void EnforceBounds()
        {
            bool changed = false;
            if (Data.RemoveAll(field => field == null) > 0) changed = true;
            if (Data.Count > MaximumFields)
            {
                Data.RemoveRange(MaximumFields, Data.Count - MaximumFields);
                changed = true;
            }
            foreach (BridgeField field in Data) if (field.EnforceBounds()) changed = true;
            if (Lines.Count > MaximumLines)
            {
                Lines.RemoveRange(MaximumLines, Lines.Count - MaximumLines);
                changed = true;
            }
            for (int i = 0; i < Lines.Count; i++)
            {
                string bounded = BridgeText.Bound(Lines[i], 16384);
                if (bounded != Lines[i]) { Lines[i] = bounded; changed = true; }
            }
            if (Warnings.Count > MaximumWarnings)
            {
                Warnings.RemoveRange(MaximumWarnings, Warnings.Count - MaximumWarnings);
                changed = true;
            }
            for (int i = Warnings.Count - 1; i >= 0; i--)
            {
                if (string.IsNullOrWhiteSpace(Warnings[i])) { Warnings.RemoveAt(i); changed = true; continue; }
                string bounded = BridgeText.Bound(Warnings[i], 4096);
                if (bounded != Warnings[i]) { Warnings[i] = bounded; changed = true; }
            }
            changed |= BoundProperty(RequestId, 256, out string requestId); RequestId = requestId;
            changed |= BoundProperty(CorrelationId, 256, out string correlationId); CorrelationId = correlationId;
            changed |= BoundProperty(AgentId, 128, out string agentId); AgentId = agentId;
            changed |= BoundProperty(ClientInstanceId, 128, out string clientInstanceId); ClientInstanceId = clientInstanceId;
            changed |= BoundProperty(ParticipantId, 128, out string participantId); ParticipantId = participantId;
            changed |= BoundProperty(SessionId, 256, out string sessionId); SessionId = sessionId;
            changed |= BoundProperty(ConnectionSessionId, 256, out string connectionSessionId); ConnectionSessionId = connectionSessionId;
            changed |= BoundProperty(Command, 128, out string command); Command = command;
            changed |= BoundProperty(OperationId, 256, out string operationId); OperationId = operationId;
            changed |= BoundProperty(OperationKind, 64, out string operationKind); OperationKind = operationKind;
            changed |= BoundProperty(OperationState, 64, out string operationState); OperationState = operationState;
            changed |= BoundProperty(CompatibilityKey, 256, out string compatibilityKey); CompatibilityKey = compatibilityKey;
            changed |= BoundProperty(DesiredState, 64, out string desiredState); DesiredState = desiredState;
            changed |= BoundProperty(RuntimeSlotId, 128, out string runtimeSlotId); RuntimeSlotId = runtimeSlotId;
            changed |= BoundProperty(DeploymentId, 256, out string deploymentId); DeploymentId = deploymentId;
            changed |= BoundProperty(ArtifactFingerprint, 256, out string artifactFingerprint); ArtifactFingerprint = artifactFingerprint;
            changed |= BoundProperty(LoadedAssemblyFingerprint, 256, out string loadedAssemblyFingerprint); LoadedAssemblyFingerprint = loadedAssemblyFingerprint;
            changed |= BoundProperty(ProcessStartIdentity, 256, out string processStartIdentity); ProcessStartIdentity = processStartIdentity;
            changed |= BoundProperty(NextAction, 512, out string nextAction); NextAction = nextAction;
            changed |= BoundProperty(CapacityState, 128, out string capacityState); CapacityState = capacityState;
            changed |= BoundProperty(Provider, 256, out string provider); Provider = provider;
            changed |= BoundProperty(ProviderVersion, 128, out string providerVersion); ProviderVersion = providerVersion;
            changed |= BoundProperty(Schema, 256, out string schema); Schema = schema;
            changed |= BoundProperty(ContinuationCursor, 8192, out string cursor); ContinuationCursor = cursor;
            changed |= BoundProperty(MutationSummary, 4096, out string mutation); MutationSummary = mutation;
            if (changed) Truncated = true;
        }

        private static bool BoundProperty(string value, int maximumCharacters, out string bounded)
        {
            if (value == null) { bounded = null; return false; }
            bounded = BridgeText.Bound(value, maximumCharacters);
            return bounded != value;
        }

        public static BridgeResult Ok(string schema = "core.result") =>
            new BridgeResult { Status = BridgeStatus.OK, Schema = schema };

        public static BridgeResult Fail(BridgeStatus status, string code, string detail = null)
        {
            BridgeResult result = new BridgeResult { Status = status };
            if (!string.IsNullOrEmpty(code)) result.Add("error", code);
            if (!string.IsNullOrEmpty(detail)) result.Add("detail", detail);
            return result;
        }

        public static BridgeResult FromLegacy(IEnumerable<string> values)
        {
            BridgeResult result = Ok("legacy.lines");
            foreach (string value in values ?? Enumerable.Empty<string>())
            {
                if (result.Lines.Count >= MaximumLines)
                {
                    result.Truncated = true;
                    break;
                }
                result.AddLine(value);
            }
            string joined = string.Join("\n", result.Lines);
            if (result.Lines.Count == 0)
                return Fail(BridgeStatus.ERROR, "empty_adapter_response");
            if (ContainsSemantic(joined, "not_found") || ContainsSemantic(joined, "not found"))
                result.Status = BridgeStatus.NOT_FOUND;
            else if (result.Lines.Any(line => line.IndexOf("=invalid", StringComparison.OrdinalIgnoreCase) >= 0) ||
                ContainsSemantic(joined, "expected:"))
                result.Status = BridgeStatus.INVALID_ARGUMENT;
            else if (ContainsSemantic(joined, "no active map") || ContainsSemantic(joined, "map=none"))
                result.Status = BridgeStatus.UNAVAILABLE;
            else if (result.Lines.Any(line => line.StartsWith("error=", StringComparison.OrdinalIgnoreCase)))
                result.Status = BridgeStatus.ERROR;
            else if (result.Lines.Any(ContainsFailureToken))
                result.Status = BridgeStatus.ERROR;
            return result;
        }

        private static bool ContainsSemantic(string value, string token) =>
            value?.IndexOf(token, StringComparison.OrdinalIgnoreCase) >= 0;

        private static bool ContainsFailureToken(string line)
        {
            string value = (line ?? string.Empty).ToUpperInvariant();
            int index = value.IndexOf("=FAIL", StringComparison.Ordinal);
            if (index < 0) index = value.IndexOf(":FAIL", StringComparison.Ordinal);
            if (index < 0) return false;
            int after = index + 5;
            return after >= value.Length || char.IsWhiteSpace(value[after]) || value[after] == ':' ||
                value[after] == '|' || value[after] == ';';
        }
    }

    public sealed class BridgeRequest
    {
        public string RequestId { get; set; }
        public string AgentId { get; set; }
        public string ClientInstanceId { get; set; }
        public string ClientCredential { get; set; }
        public string ParticipantId { get; set; }
        public string ConnectionSessionId { get; set; }
        public string CorrelationId { get; set; }
        public string WorkspaceId { get; set; }
        public string SessionId { get; set; }
        public string Command { get; set; }
        public string OperationId { get; set; }
        public string OperationKind { get; set; }
        public string DesiredState { get; set; }
        public string CompatibilityKey { get; set; }
        public string ManagedProfile { get; set; }
        public string RimWorldVersion { get; set; }
        public string ModSetFingerprint { get; set; }
        public string ModLoadOrderFingerprint { get; set; }
        public string SourceBuildIdentity { get; set; }
        public string ExpectedCoreFingerprint { get; set; }
        public string ExpectedAdapterFingerprint { get; set; }
        public string ExpectedLoadedAssemblyFingerprint { get; set; }
        public string ConfigurationFingerprint { get; set; }
        public string UserRootFingerprint { get; set; }
        public string SaveTarget { get; set; }
        public string MapTarget { get; set; }
        public bool RequiresProcessReplacement { get; set; }
        public bool KeepRunning { get; set; }
        public long LifecycleGeneration { get; set; }
        public string MutationScope { get; set; }
        public string RuntimeSlotId { get; set; }
        public string DeploymentId { get; set; }
        public string ArtifactFingerprint { get; set; }
        public int ExpectedProcessId { get; set; }
        public string ExpectedProcessStartIdentity { get; set; }
        public string ExpectedProcessSessionId { get; set; }
        public long ExpectedProcessLifecycleGeneration { get; set; }
        public string Argument { get; set; }
        public DateTime EnqueuedUtc { get; set; }
        public DateTime ReceivedUtc { get; set; }
        public DateTime DeadlineUtc { get; set; }
        public string IdempotencyKey { get; set; }
        public string OutputFormat { get; set; } = "line";
        public string DetailLevel { get; set; } = "compact";
        public bool AllowExpensive { get; set; }
        public BridgeCommandMode Mode { get; set; }
        public BridgeCostClass Cost { get; set; }
        public volatile bool Cancelled;
        public volatile bool ClientDisconnected;
        public volatile bool Started;
        internal string AuthToken;
        internal int TransportGeneration;
        internal string PreparedAdapterId;
        internal string PreparedAdapterGeneration;
        internal object PreparedAdapter;
        internal BridgeCommandDescriptor PreparedDescriptor;
        internal object PreparedPayload;
        internal int NestingDepth;
        internal bool IdempotentReplay;
        internal bool ExecutionReached;
        internal long QueueBarrierId;
        internal double PreparationMs;
        internal string MutationGameIdentity;
        internal string MutationSaveIdentity;
        internal string MutationConfirmationState;
        internal bool MutationGameLoaded;
        internal bool MutationSettingEnabled;
        internal string AuthorizedLeaseContext;
        internal DateTime? AuthorizedLeaseExpiresUtc;
        internal object CooperativeState;
        internal bool YieldExecution;
        internal double CooperativeExecutionMs;
        internal int CooperativeSteps;
        internal double CooperativeMaxStepMs;
        internal bool CooperativeMainThreadOverrun;
        internal bool SharedOperationRegistered;
        internal int SharedOperationCompletionClaimed;
        internal readonly ManualResetEventSlim Done = new ManualResetEventSlim(false);
        internal BridgeResult Result;

        public bool Expired => DateTime.UtcNow >= DeadlineUtc;
        public TimeSpan Remaining => DeadlineUtc - DateTime.UtcNow;
    }

    public sealed class BridgeExecutionContext
    {
        private readonly Func<bool> cancellation;
        public BridgeRequest Request { get; }
        public string SessionId => Request.SessionId;
        public Map Map { get; }
        public int Tick => BridgeGameState.TickManager?.TicksGame ?? -1;
        public DateTime DeadlineUtc => Request.DeadlineUtc;
        public bool IsCancellationRequested => cancellation?.Invoke() == true || Request.Expired;

        internal BridgeExecutionContext(BridgeRequest request, Map map, Func<bool> cancellation)
        {
            Request = request;
            Map = map;
            this.cancellation = cancellation;
        }

        public void ThrowIfCancellationRequested()
        {
            if (IsCancellationRequested) throw new OperationCanceledException("Bridge request was cancelled or expired.");
        }

        public string SafeOutputPath(string category, string fileName)
        {
            return BridgePaths.SafeOutputPath(category, fileName);
        }
    }

    public sealed class BridgeAdapterMetadata
    {
        public string Id { get; set; }
        public string DisplayName { get; set; }
        public string Version { get; set; }
        public string Generation { get; set; }
        public string ChangeSummary { get; set; }
    }

    public interface IBridgeAdapterProvider
    {
        BridgeAdapterMetadata Metadata { get; }
        IEnumerable<BridgeCommandDescriptor> Commands { get; }
        BridgeResult Execute(BridgeExecutionContext context);
    }

    // Opt-in contract for adapters that can perform bounded work and yield between frames.
    public interface IBridgeCooperativeAdapterProvider : IBridgeAdapterProvider
    {
        string ExecutionContract { get; }
        IBridgeCooperativeAdapterExecution BeginCooperativeExecution(BridgeExecutionContext context);
    }

    public interface IBridgeCooperativeAdapterExecution
    {
        bool IsComplete { get; }
        BridgeResult Step(BridgeExecutionContext context);
    }

    internal static class BridgeText
    {
        internal static string Clean(string value)
        {
            return (value ?? "none").Replace('|', '/').Replace('\r', ' ').Replace('\n', ' ');
        }

        internal static string Invariant(object value)
        {
            if (value == null) return "null";
            if (value is bool boolean) return boolean ? "true" : "false";
            if (value is IFormattable formattable)
                return Clean(formattable.ToString(null, System.Globalization.CultureInfo.InvariantCulture));
            return Clean(value.ToString());
        }

        internal static string Bound(object value, int maximumCharacters)
        {
            string text = Invariant(value);
            if (text.Length <= maximumCharacters) return text;
            return text.Substring(0, Math.Max(0, maximumCharacters - 14)) + "...<truncated>";
        }

        internal static string NormalizeCommand(string command)
        {
            return (command ?? string.Empty).Trim().ToUpperInvariant();
        }
    }

    internal static class BridgeTiming
    {
        internal static double Milliseconds(long start) =>
            (Stopwatch.GetTimestamp() - start) * 1000d / Stopwatch.Frequency;
    }

    internal static class BridgeGameState
    {
        internal static Map CurrentMap => Current.Game == null ? null : Find.CurrentMap;
        internal static TickManager TickManager => Current.Game == null ? null : Find.TickManager;
        internal static List<Map> Maps => Current.Game == null ? null : Find.Maps;
        internal static World World => Current.Game == null ? null : Find.World;
    }
}
