using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Runtime.Serialization.Json;
using System.Text;

namespace RimWorldDevBridge
{
    public static class BridgeProtocol
    {
        public const string BridgeVersion = "2.2.0";
        public const int ProtocolVersion = 10;
        public const string CoreSchema = "v10.1-typed-core";
        public const int MaxRequestBytes = 32768;
        public const int MaxArgumentBytes = 24576;
        public const int MaxResponseBytes = 262144;
        public const int MaxLineBytes = 4096;
        public const int MaxBatchSections = 12;
        public const int MaxMacroCalls = 16;
        public const int MaxPageSize = 200;
        public const int DefaultPageSize = 50;
        public const int DefaultDeadlineMs = 4500;
        public const int MaximumDeadlineMs = 120000;

        public static bool TryParse(string raw, string currentSessionId, out BridgeRequest request,
            out BridgeResult failure)
        {
            request = null;
            failure = null;
            if (raw == null)
            {
                failure = BridgeResult.Fail(BridgeStatus.INVALID_ARGUMENT, "empty_request");
                return false;
            }
            if (Encoding.UTF8.GetByteCount(raw) > MaxRequestBytes)
            {
                failure = BridgeResult.Fail(BridgeStatus.INVALID_ARGUMENT, "request_too_large");
                return false;
            }
            string line = raw.Replace('\r', '\n').Split(new[] { '\n' },
                StringSplitOptions.RemoveEmptyEntries).FirstOrDefault() ?? string.Empty;
            string[] parts = line.Split(new[] { '|' }, 4);
            if (parts.Length < 2)
            {
                failure = BridgeResult.Fail(BridgeStatus.INVALID_ARGUMENT, "invalid_request_shape",
                    "Expected id|COMMAND|argument|options.");
                return false;
            }
            string id = parts[0].Trim();
            string command = BridgeText.NormalizeCommand(parts[1]);
            string argument = parts.Length > 2 ? parts[2] : string.Empty;
            if (id.Length == 0 || id.Length > 96 || command.Length == 0 || command.Length > 96)
            {
                failure = BridgeResult.Fail(BridgeStatus.INVALID_ARGUMENT, "invalid_request_identity");
                return false;
            }
            if (Encoding.UTF8.GetByteCount(argument) > MaxArgumentBytes)
            {
                failure = BridgeResult.Fail(BridgeStatus.INVALID_ARGUMENT, "argument_too_large");
                return false;
            }
            Dictionary<string, string> options;
            try
            {
                options = ParseOptions(parts.Length > 3 ? parts[3] : string.Empty);
            }
            catch (Exception exception)
            {
                failure = BridgeResult.Fail(BridgeStatus.INVALID_ARGUMENT, "invalid_request_options",
                    exception.GetBaseException().Message);
                return false;
            }
            int timeoutMs = DefaultDeadlineMs;
            string timeoutValue = Value(options, "timeoutMs");
            if (!string.IsNullOrEmpty(timeoutValue) &&
                (!int.TryParse(timeoutValue, out timeoutMs) || timeoutMs < 50 || timeoutMs > MaximumDeadlineMs))
            {
                failure = BridgeResult.Fail(BridgeStatus.INVALID_ARGUMENT, "invalid_timeout",
                    "timeoutMs must be an integer from 50 through " + MaximumDeadlineMs + ".");
                return false;
            }
            DateTime receivedUtc = DateTime.UtcNow;
            int progressTimeoutMs = ParseBoundedInt(Value(options, "progressTimeoutMs"), 30000, 1000, 3600000);
            request = new BridgeRequest
            {
                RequestId = id,
                CorrelationId = Value(options, "correlationId") ?? id,
                AgentId = Value(options, "agentId") ?? "anonymous",
                ClientInstanceId = Value(options, "clientInstanceId") ?? "client-legacy",
                ClientCredential = Value(options, "clientCredential"),
                ConnectionSessionId = Value(options, "connectionSessionId") ?? currentSessionId,
                ParticipantId = Value(options, "participantId") ?? "participant-" + id,
                WorkspaceId = Value(options, "workspaceId") ?? "default",
                SessionId = Value(options, "session") ?? currentSessionId,
                Command = command,
                Argument = argument,
                ReceivedUtc = receivedUtc,
                EnqueuedUtc = receivedUtc,
                DeadlineUtc = receivedUtc.AddMilliseconds(timeoutMs),
                IdempotencyKey = Value(options, "idempotency"),
                OutputFormat = NormalizeFormat(Value(options, "format")),
                DetailLevel = Value(options, "detail") ?? "compact",
                AllowExpensive = ParseBool(Value(options, "allowExpensive")),
                AuthToken = Value(options, "lease"),
                OperationId = Value(options, "operationId"),
                OperationKind = Value(options, "operationKind"),
                GoalId = Value(options, "goalId"),
                RequestedWorkflow = Value(options, "requestedWorkflow"),
                AuthorizationReference = Value(options, "authorizationReference"),
                ProgressDeadlineUtc = receivedUtc.AddMilliseconds(progressTimeoutMs),
                DesiredState = Value(options, "desiredState"),
                CompatibilityKey = Value(options, "compatibilityKey"),
                ManagedProfile = Value(options, "managedProfile"),
                RimWorldVersion = Value(options, "rimWorldVersion"),
                ModSetFingerprint = Value(options, "modSetFingerprint"),
                ModLoadOrderFingerprint = Value(options, "modLoadOrderFingerprint"),
                 SourceBuildIdentity = Value(options, "sourceBuildIdentity"),
                 ExpectedCoreFingerprint = Value(options, "expectedCoreFingerprint"),
                 ExpectedAdapterFingerprint = Value(options, "expectedAdapterFingerprint"),
                 ExpectedLoadedAssemblyFingerprint = Value(options, "loadedAssemblyFingerprint"),
                ConfigurationFingerprint = Value(options, "configurationFingerprint"),
                UserRootFingerprint = Value(options, "userRootFingerprint"),
                SaveTarget = Value(options, "saveTarget"),
                 MapTarget = Value(options, "mapTarget"),
                 RequiresProcessReplacement = ParseBool(Value(options, "requiresProcessReplacement")),
                 KeepRunning = ParseBool(Value(options, "keepRunning")),
                 LifecycleGeneration = ParseLong(Value(options, "lifecycleGeneration")),
                MutationScope = Value(options, "mutationScope"),
                 RuntimeSlotId = Value(options, "runtimeSlotId"),
                 DeploymentId = Value(options, "deploymentId"),
                 ArtifactFingerprint = Value(options, "artifactFingerprint"),
                 ExpectedProcessId = ParseBoundedInt(Value(options, "expectedProcessId"), 0, 0, 2147483647),
                 ExpectedProcessStartIdentity = Value(options, "expectedProcessStartIdentity"),
                 ExpectedProcessSessionId = Value(options, "expectedProcessSessionId"),
                 ExpectedProcessLifecycleGeneration = ParseLong(Value(options, "expectedProcessLifecycleGeneration"))
            };
            if (!ValidOptionValue(request.AgentId, 128) || !ValidOptionValue(request.ClientInstanceId, 128) ||
                !ValidOptionValue(request.ClientCredential, 256) ||
                !ValidOptionValue(request.ConnectionSessionId, 128) || !ValidOptionValue(request.ParticipantId, 128) ||
                !ValidOptionValue(request.CorrelationId, 128) || !ValidOptionValue(request.WorkspaceId, 128) ||
                !ValidOptionValue(request.SessionId, 128) || !ValidOptionValue(request.IdempotencyKey, 128) ||
                !ValidOptionValue(request.AuthToken, 128) || !ValidOptionValue(request.DetailLevel, 32) ||
                 !ValidOptionValue(request.OperationId, 256) || !ValidOptionValue(request.OperationKind, 64) ||
                 !ValidOptionValue(request.GoalId, 256) ||
                 !ValidOptionValue(request.RequestedWorkflow, 128) ||
                 !ValidOptionValue(request.AuthorizationReference, 256) ||
                 !ValidOptionValue(request.DesiredState, 64) || !ValidOptionValue(request.CompatibilityKey, 256) ||
                 !ValidOptionValue(request.RuntimeSlotId, 128) ||
                 !ValidOptionValue(request.DeploymentId, 256) || !ValidOptionValue(request.ArtifactFingerprint, 256) ||
                 !ValidOptionValue(request.ExpectedProcessStartIdentity, 256) ||
                 !ValidOptionValue(request.ExpectedProcessSessionId, 256) ||
                !ValidOptionValue(request.ManagedProfile, 128) || !ValidOptionValue(request.RimWorldVersion, 64) ||
                !ValidOptionValue(request.ModSetFingerprint, 256) ||
                !ValidOptionValue(request.ModLoadOrderFingerprint, 256) ||
                !ValidOptionValue(request.SourceBuildIdentity, 256) ||
                 !ValidOptionValue(request.ExpectedCoreFingerprint, 256) ||
                 !ValidOptionValue(request.ExpectedAdapterFingerprint, 256) ||
                 !ValidOptionValue(request.ExpectedLoadedAssemblyFingerprint, 256) ||
                !ValidOptionValue(request.ConfigurationFingerprint, 256) ||
                !ValidOptionValue(request.UserRootFingerprint, 256) ||
                !ValidOptionValue(request.SaveTarget, 256) || !ValidOptionValue(request.MapTarget, 256) ||
                !ValidOptionValue(request.MutationScope, 128))
            {
                request = null;
                failure = BridgeResult.Fail(BridgeStatus.INVALID_ARGUMENT, "invalid_request_option_length");
                return false;
            }
            return true;
        }

        public static string Serialize(BridgeResult result, string format)
        {
            result?.EnforceBounds();
            if (string.Equals(format, "json", StringComparison.OrdinalIgnoreCase))
                return SerializeJson(result);
            return SerializeLines(result);
        }

        public static string SerializeLines(BridgeResult result)
        {
            result?.EnforceBounds();
            List<string> lines = new List<string>
            {
                "id=" + BridgeText.Clean(result?.RequestId ?? "unknown"),
                "status=" + (result?.Status.ToString() ?? BridgeStatus.ERROR.ToString()),
                "session=" + BridgeText.Clean(result?.SessionId ?? "none"),
                "command=" + BridgeText.Clean(result?.Command ?? "none"),
                "provider=" + BridgeText.Clean(result?.Provider ?? "core") + " version:" +
                    BridgeText.Clean(result?.ProviderVersion ?? BridgeVersion),
                "mode=" + (result?.Mode.ToString() ?? BridgeCommandMode.PureRead.ToString()),
                "schema=" + BridgeText.Clean(result?.Schema ?? "core.result") + " version:" +
                    (result?.SchemaVersion ?? 1),
                "timing=prepareMs:" + (result?.PreparationMs ?? 0d).ToString("0.###",
                    System.Globalization.CultureInfo.InvariantCulture) + " queueMs:" +
                    (result?.QueueDelayMs ?? 0d).ToString("0.###",
                    System.Globalization.CultureInfo.InvariantCulture) + " executionMs:" +
                    (result?.ExecutionMs ?? 0d).ToString("0.###", System.Globalization.CultureInfo.InvariantCulture),
                "ticks=before:" + (result?.TickBefore ?? -1) + " after:" + (result?.TickAfter ?? -1),
                "mutation=" + BridgeText.Clean(result?.MutationSummary ?? "none")
            };
            if (result != null)
            {
                if (!string.IsNullOrWhiteSpace(result.OperationId))
                    lines.Add("operation=" + BridgeText.Clean(result.OperationId) + " state=" +
                        BridgeText.Clean(result.OperationState) + " phase=" + BridgeText.Clean(result.OperationPhase));
                if (!string.IsNullOrWhiteSpace(result.GoalId))
                    lines.Add("goal=" + BridgeText.Clean(result.GoalId));
                if (!string.IsNullOrWhiteSpace(result.CleanupStatus))
                    lines.Add("cleanup=" + BridgeText.Clean(result.CleanupStatus));
                if (!string.IsNullOrWhiteSpace(result.CapabilityVersion))
                    lines.Add("capabilityVersion=" + BridgeText.Clean(result.CapabilityVersion));
                lines.AddRange(result.Data.Select(field => BridgeText.Clean(field.Name) + "=" +
                    BridgeText.Clean(field.Value)));
                lines.AddRange(result.Lines.Select(BridgeText.Clean));
                lines.AddRange(result.Warnings.Select(value => "warning=" + BridgeText.Clean(value)));
                if (result.Truncated)
                    lines.Add("truncated=true cursor:" + BridgeText.Clean(result.ContinuationCursor ?? "none"));
            }
            return BoundLines(lines, result);
        }

        private static string SerializeJson(BridgeResult result)
        {
            result?.EnforceBounds();
            JsonResult payload = JsonResult.From(result);
            string value;
            using (MemoryStream stream = new MemoryStream())
            {
                new DataContractJsonSerializer(typeof(JsonResult)).WriteObject(stream, payload);
                value = Encoding.UTF8.GetString(stream.ToArray());
            }
            if (Encoding.UTF8.GetByteCount(value) <= MaxResponseBytes) return value;
            result.Truncated = true;
            result.Warn("JSON payload exceeded responseBytes; line-compatible metadata was returned.");
            return SerializeLines(result);
        }

        private static string BoundLines(IEnumerable<string> source, BridgeResult result)
        {
            StringBuilder output = new StringBuilder();
            int outputBytes = 0;
            bool lineTruncated = false;
            bool responseTruncated = false;
            foreach (string original in source ?? Enumerable.Empty<string>())
            {
                if (Encoding.UTF8.GetByteCount(original ?? string.Empty) > MaxLineBytes)
                {
                    lineTruncated = true;
                    if (result != null) result.Truncated = true;
                }
                string line = BoundUtf8(original ?? string.Empty, MaxLineBytes);
                int candidate = Encoding.UTF8.GetByteCount(line) + (output.Length == 0 ? 0 : 1);
                const string marker = "truncated=true reason:responseBytes";
                int reserve = Encoding.UTF8.GetByteCount(marker) + 1;
                if (outputBytes + candidate > MaxResponseBytes - reserve)
                {
                    if (result != null) result.Truncated = true;
                    Append(output, "truncated=true reason:responseBytes");
                    responseTruncated = true;
                    break;
                }
                Append(output, line);
                outputBytes += candidate;
            }
            if (lineTruncated && !responseTruncated)
            {
                const string marker = "truncated=true reason:lineBytes";
                int markerBytes = Encoding.UTF8.GetByteCount(marker) + (output.Length == 0 ? 0 : 1);
                if (outputBytes + markerBytes <= MaxResponseBytes) Append(output, marker);
            }
            return output.ToString();
        }

        private static string BoundUtf8(string value, int maxBytes)
        {
            if (Encoding.UTF8.GetByteCount(value) <= maxBytes) return value;
            int count = Math.Min(value.Length, maxBytes / 2);
            while (count > 0 && Encoding.UTF8.GetByteCount(value.Substring(0, count)) > maxBytes - 16) count--;
            return value.Substring(0, count) + "...<truncated>";
        }

        private static void Append(StringBuilder builder, string line)
        {
            if (builder.Length > 0) builder.Append('\n');
            builder.Append(line);
        }

        internal static Dictionary<string, string> ParseOptions(string value)
        {
            Dictionary<string, string> result = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
            foreach (string entry in (value ?? string.Empty).Split(new[] { '&' }, StringSplitOptions.RemoveEmptyEntries))
            {
                int split = entry.IndexOf('=');
                if (split <= 0) continue;
                string rawKey = entry.Substring(0, split);
                string rawItem = entry.Substring(split + 1);
                if (!HasValidPercentEncoding(rawKey) || !HasValidPercentEncoding(rawItem))
                    throw new InvalidDataException("Option percent-encoding is malformed.");
                string key = Uri.UnescapeDataString(rawKey);
                string item = Uri.UnescapeDataString(rawItem);
                if (result.ContainsKey(key)) throw new InvalidDataException("Duplicate bridge option.");
                result[key] = item;
            }
            return result;
        }

        private static bool HasValidPercentEncoding(string value)
        {
            for (int i = 0; i < (value?.Length ?? 0); i++)
            {
                if (value[i] != '%') continue;
                if (i + 2 >= value.Length || !Uri.IsHexDigit(value[i + 1]) || !Uri.IsHexDigit(value[i + 2]))
                    return false;
                i += 2;
            }
            return true;
        }

        internal static string Value(IDictionary<string, string> values, string key)
        {
            return values != null && values.TryGetValue(key, out string value) ? value : null;
        }

        internal static bool ParseBool(string value) =>
            string.Equals(value, "true", StringComparison.OrdinalIgnoreCase) || value == "1";

        internal static long ParseLong(string value)
        {
            return long.TryParse(value, out long parsed) ? parsed : 0L;
        }

        internal static int ParseBoundedInt(string value, int fallback, int minimum, int maximum)
        {
            return int.TryParse(value, out int parsed) ? Math.Max(minimum, Math.Min(maximum, parsed)) : fallback;
        }

        private static string NormalizeFormat(string value) =>
            string.Equals(value, "json", StringComparison.OrdinalIgnoreCase) ? "json" : "line";

        private static bool ValidOptionValue(string value, int maximumLength) =>
            value == null || value.Length <= maximumLength && value.IndexOf('\0') < 0;

        [System.Runtime.Serialization.DataContract]
        private sealed class JsonResult
        {
            [System.Runtime.Serialization.DataMember(Order = 1)] public string requestId;
            [System.Runtime.Serialization.DataMember(Order = 2)] public string correlationId;
            [System.Runtime.Serialization.DataMember(Order = 3)] public string agentId;
            [System.Runtime.Serialization.DataMember(Order = 4)] public string clientInstanceId;
            [System.Runtime.Serialization.DataMember(Order = 5)] public string participantId;
            [System.Runtime.Serialization.DataMember(Order = 6)] public string sessionId;
            [System.Runtime.Serialization.DataMember(Order = 7)] public string connectionSessionId;
            [System.Runtime.Serialization.DataMember(Order = 8)] public string command;
            [System.Runtime.Serialization.DataMember(Order = 9)] public string operationId;
            [System.Runtime.Serialization.DataMember(Order = 10)] public string operationKind;
            [System.Runtime.Serialization.DataMember(Order = 11)] public string operationState;
            [System.Runtime.Serialization.DataMember(Order = 12)] public string compatibilityKey;
            [System.Runtime.Serialization.DataMember(Order = 13)] public string desiredState;
            [System.Runtime.Serialization.DataMember(Order = 14)] public string runtimeSlotId;
            [System.Runtime.Serialization.DataMember(Order = 15)] public string deploymentId;
            [System.Runtime.Serialization.DataMember(Order = 16)] public string artifactFingerprint;
            [System.Runtime.Serialization.DataMember(Order = 17)] public string loadedAssemblyFingerprint;
            [System.Runtime.Serialization.DataMember(Order = 18)] public int pid;
            [System.Runtime.Serialization.DataMember(Order = 19)] public string processStartIdentity;
            [System.Runtime.Serialization.DataMember(Order = 20)] public long lifecycleGeneration;
            [System.Runtime.Serialization.DataMember(Order = 21)] public long progressSequence;
            [System.Runtime.Serialization.DataMember(Order = 22)] public DateTime? lastProgressAtUtc;
            [System.Runtime.Serialization.DataMember(Order = 23)] public bool terminal;
            [System.Runtime.Serialization.DataMember(Order = 24)] public bool recoverable;
            [System.Runtime.Serialization.DataMember(Order = 25)] public bool retrySafe;
            [System.Runtime.Serialization.DataMember(Order = 26)] public string nextAction;
            [System.Runtime.Serialization.DataMember(Order = 27)] public string capacityState;
            [System.Runtime.Serialization.DataMember(Order = 28)] public bool keepRunning;
            [System.Runtime.Serialization.DataMember(Order = 29)] public string provider;
            [System.Runtime.Serialization.DataMember(Order = 30)] public string providerVersion;
            [System.Runtime.Serialization.DataMember(Order = 31)] public string mode;
            [System.Runtime.Serialization.DataMember(Order = 32)] public string status;
            [System.Runtime.Serialization.DataMember(Order = 33)] public string schema;
            [System.Runtime.Serialization.DataMember(Order = 34)] public int schemaVersion;
            [System.Runtime.Serialization.DataMember(Order = 35)] public double queueDelayMs;
            [System.Runtime.Serialization.DataMember(Order = 36)] public double executionMs;
            [System.Runtime.Serialization.DataMember(Order = 37)] public double preparationMs;
            [System.Runtime.Serialization.DataMember(Order = 38)] public int tickBefore;
            [System.Runtime.Serialization.DataMember(Order = 39)] public int tickAfter;
            [System.Runtime.Serialization.DataMember(Order = 40)] public bool truncated;
            [System.Runtime.Serialization.DataMember(Order = 41)] public string cursor;
            [System.Runtime.Serialization.DataMember(Order = 42)] public string mutation;
            [System.Runtime.Serialization.DataMember(Order = 43)] public List<BridgeField> data;
            [System.Runtime.Serialization.DataMember(Order = 44)] public List<string> lines;
            [System.Runtime.Serialization.DataMember(Order = 45)] public List<string> warnings;
            [System.Runtime.Serialization.DataMember(Order = 46)] public int operationVersion;
            [System.Runtime.Serialization.DataMember(Order = 47)] public string operationPhase;
            [System.Runtime.Serialization.DataMember(Order = 48)] public List<string> completedPhases;
            [System.Runtime.Serialization.DataMember(Order = 49)] public string requestedWorkflow;
            [System.Runtime.Serialization.DataMember(Order = 50)] public DateTime? operationDeadlineUtc;
            [System.Runtime.Serialization.DataMember(Order = 51)] public DateTime? progressDeadlineUtc;
            [System.Runtime.Serialization.DataMember(Order = 52)] public string authorizationReference;
            [System.Runtime.Serialization.DataMember(Order = 53)] public string terminalResultCode;
            [System.Runtime.Serialization.DataMember(Order = 54)] public string terminalResultDetail;
            [System.Runtime.Serialization.DataMember(Order = 55)] public string cleanupStatus;
            [System.Runtime.Serialization.DataMember(Order = 56)] public string capabilityVersion;
            [System.Runtime.Serialization.DataMember(Order = 57)] public List<string> supportedOperationStates;
            [System.Runtime.Serialization.DataMember(Order = 58)] public List<string> supportedOperationKinds;
            [System.Runtime.Serialization.DataMember(Order = 59)] public List<string> readOperations;
            [System.Runtime.Serialization.DataMember(Order = 60)] public List<string> mutationClasses;
            [System.Runtime.Serialization.DataMember(Order = 61)] public int supportedRuntimeSlotCount;
            [System.Runtime.Serialization.DataMember(Order = 62)] public bool concurrentReadDiagnostics;
            [System.Runtime.Serialization.DataMember(Order = 63)] public string buildProvider;
            [System.Runtime.Serialization.DataMember(Order = 64)] public string deploymentProvider;
            [System.Runtime.Serialization.DataMember(Order = 65)] public bool adapterReloadSupported;
            [System.Runtime.Serialization.DataMember(Order = 66)] public bool saveFixtureSupported;
            [System.Runtime.Serialization.DataMember(Order = 67)] public List<string> evidenceTypes;
            [System.Runtime.Serialization.DataMember(Order = 68)] public string authorizationMechanism;
             [System.Runtime.Serialization.DataMember(Order = 69)] public List<string> platformRestrictions;
             [System.Runtime.Serialization.DataMember(Order = 70)] public string goalId;

            internal static JsonResult From(BridgeResult result) => new JsonResult
            {
                requestId = result?.RequestId,
                correlationId = result?.CorrelationId,
                agentId = result?.AgentId,
                clientInstanceId = result?.ClientInstanceId,
                participantId = result?.ParticipantId,
                sessionId = result?.SessionId,
                connectionSessionId = result?.ConnectionSessionId,
                command = result?.Command,
                 operationId = result?.OperationId,
                 goalId = result?.GoalId,
                operationKind = result?.OperationKind,
                operationState = result?.OperationState,
                compatibilityKey = result?.CompatibilityKey,
                desiredState = result?.DesiredState,
                runtimeSlotId = result?.RuntimeSlotId,
                deploymentId = result?.DeploymentId,
                artifactFingerprint = result?.ArtifactFingerprint,
                loadedAssemblyFingerprint = result?.LoadedAssemblyFingerprint,
                pid = result?.ProcessId ?? 0,
                processStartIdentity = result?.ProcessStartIdentity,
                lifecycleGeneration = result?.LifecycleGeneration ?? 0,
                progressSequence = result?.ProgressSequence ?? 0,
                lastProgressAtUtc = result?.LastProgressAtUtc,
                terminal = result?.Terminal ?? false,
                recoverable = result?.Recoverable ?? false,
                retrySafe = result?.RetrySafe ?? false,
                nextAction = result?.NextAction,
                capacityState = result?.CapacityState,
                keepRunning = result?.KeepRunning ?? false,
                provider = result?.Provider,
                providerVersion = result?.ProviderVersion,
                mode = result?.Mode.ToString(),
                status = result?.Status.ToString(),
                schema = result?.Schema,
                schemaVersion = result?.SchemaVersion ?? 1,
                queueDelayMs = result?.QueueDelayMs ?? 0d,
                preparationMs = result?.PreparationMs ?? 0d,
                executionMs = result?.ExecutionMs ?? 0d,
                tickBefore = result?.TickBefore ?? -1,
                tickAfter = result?.TickAfter ?? -1,
                truncated = result?.Truncated ?? false,
                cursor = result?.ContinuationCursor,
                mutation = result?.MutationSummary,
                data = result?.Data.ToList() ?? new List<BridgeField>(),
                lines = result?.Lines.ToList() ?? new List<string>(),
                warnings = result?.Warnings.ToList() ?? new List<string>()
                ,operationVersion = result?.OperationVersion ?? 0
                ,operationPhase = result?.OperationPhase
                ,completedPhases = result?.CompletedPhases.ToList() ?? new List<string>()
                ,requestedWorkflow = result?.RequestedWorkflow
                ,operationDeadlineUtc = result?.OperationDeadlineUtc
                ,progressDeadlineUtc = result?.ProgressDeadlineUtc
                ,authorizationReference = result?.AuthorizationReference
                ,terminalResultCode = result?.TerminalResultCode
                ,terminalResultDetail = result?.TerminalResultDetail
                ,cleanupStatus = result?.CleanupStatus
                ,capabilityVersion = result?.CapabilityVersion
                ,supportedOperationStates = result?.SupportedOperationStates.ToList() ?? new List<string>()
                ,supportedOperationKinds = result?.SupportedOperationKinds.ToList() ?? new List<string>()
                ,readOperations = result?.ReadOperations.ToList() ?? new List<string>()
                ,mutationClasses = result?.MutationClasses.ToList() ?? new List<string>()
                ,supportedRuntimeSlotCount = result?.SupportedRuntimeSlotCount ?? 0
                ,concurrentReadDiagnostics = result?.ConcurrentReadDiagnostics ?? false
                ,buildProvider = result?.BuildProvider
                ,deploymentProvider = result?.DeploymentProvider
                ,adapterReloadSupported = result?.AdapterReloadSupported ?? false
                ,saveFixtureSupported = result?.SaveFixtureSupported ?? false
                ,evidenceTypes = result?.EvidenceTypes.ToList() ?? new List<string>()
                ,authorizationMechanism = result?.AuthorizationMechanism
                ,platformRestrictions = result?.PlatformRestrictions.ToList() ?? new List<string>()
            };
        }
    }

    internal sealed class BridgeQuery
    {
        internal string Filter = string.Empty;
        internal int Limit = BridgeProtocol.DefaultPageSize;
        internal int Offset;
        internal string Fields;
        internal string SnapshotId;
        internal long SnapshotExpiryTicks;
        internal string Ordering;
        internal string CursorScope => Filter + "\nfields=" + (Fields ?? string.Empty);
        internal bool UsesSnapshot => !string.IsNullOrEmpty(Ordering);

        internal static string StableOrdering(string command)
        {
            switch (BridgeText.NormalizeCommand(command))
            {
                case "PAWNS":
                case "THINGS":
                case "JOBS":
                    return "thingId:asc";
                default:
                    return string.Empty;
            }
        }

        internal static BridgeQuery Parse(string argument, string sessionId, string command,
            out BridgeResult failure)
        {
            failure = null;
            BridgeQuery query = new BridgeQuery();
            query.Ordering = StableOrdering(command);
            string value = argument ?? string.Empty;
            if (value.IndexOf('=') < 0)
            {
                query.Filter = value.Trim();
                return query;
            }
            Dictionary<string, string> options = BridgeProtocol.ParseOptions(value.Replace(';', '&'));
            query.Filter = BridgeProtocol.Value(options, "filter") ?? string.Empty;
            query.Fields = BridgeProtocol.Value(options, "fields");
            query.Limit = BridgeProtocol.ParseBoundedInt(BridgeProtocol.Value(options, "limit"),
                BridgeProtocol.DefaultPageSize, 1, BridgeProtocol.MaxPageSize);
            string cursor = BridgeProtocol.Value(options, "cursor");
            if (!string.IsNullOrEmpty(cursor))
            {
                string error;
                if (query.UsesSnapshot)
                {
                    if (!BridgeCursor.TryDecodeSnapshot(cursor, sessionId, command, query.CursorScope,
                        query.Ordering, out query.SnapshotId, out query.SnapshotExpiryTicks, out query.Offset,
                        out error))
                    {
                        failure = BridgeResult.Fail(BridgeStatus.INVALID_ARGUMENT, error);
                        return null;
                    }
                }
                else if (!BridgeCursor.TryDecode(cursor, sessionId, command, query.CursorScope, out query.Offset))
                {
                    failure = BridgeResult.Fail(BridgeStatus.INVALID_ARGUMENT, "invalid_cursor");
                    return null;
                }
            }
            return query;
        }
    }

    internal static class BridgeCursor
    {
        internal static string Encode(string session, string command, string filter, int offset)
        {
            string raw = string.Join("\n", EncodePart(session), EncodePart(command), EncodePart(filter),
                offset.ToString(System.Globalization.CultureInfo.InvariantCulture));
            return Convert.ToBase64String(Encoding.UTF8.GetBytes(raw)).TrimEnd('=').Replace('+', '-').Replace('/', '_');
        }

        internal static string EncodeSnapshot(string session, string command, string scope, string ordering,
            string snapshotId, long expiryTicks, int offset)
        {
            string raw = string.Join("\n", EncodePart("2"), EncodePart(session),
                EncodePart(BridgeText.NormalizeCommand(command)),
                EncodePart(scope), EncodePart(ordering), EncodePart(snapshotId),
                expiryTicks.ToString(System.Globalization.CultureInfo.InvariantCulture),
                offset.ToString(System.Globalization.CultureInfo.InvariantCulture));
            return Convert.ToBase64String(Encoding.UTF8.GetBytes(raw)).TrimEnd('=').Replace('+', '-').Replace('/', '_');
        }

        internal static bool TryDecode(string cursor, string session, string command, string filter, out int offset)
        {
            offset = 0;
            try
            {
                string value = cursor.Replace('-', '+').Replace('_', '/');
                value = value.PadRight((value.Length + 3) / 4 * 4, '=');
                string[] parts = Encoding.UTF8.GetString(Convert.FromBase64String(value)).Split('\n');
                return parts.Length == 4 && DecodePart(parts[0]) == (session ?? "") &&
                    DecodePart(parts[1]) == (command ?? "") && DecodePart(parts[2]) == (filter ?? "") &&
                    int.TryParse(parts[3], out offset) && offset >= 0;
            }
            catch { return false; }
        }

        internal static bool TryDecodeSnapshot(string cursor, string session, string command, string scope,
            string ordering, out string snapshotId, out long expiryTicks, out int offset, out string error)
        {
            snapshotId = null;
            expiryTicks = 0;
            offset = 0;
            error = "invalid_cursor";
            try
            {
                string value = cursor.Replace('-', '+').Replace('_', '/');
                value = value.PadRight((value.Length + 3) / 4 * 4, '=');
                string[] parts = Encoding.UTF8.GetString(Convert.FromBase64String(value)).Split('\n');
                if (parts.Length == 4)
                {
                    error = "snapshot_cursor_required";
                    return false;
                }
                if (parts.Length != 8 || DecodePart(parts[0]) != "2") return false;
                string cursorSession = DecodePart(parts[1]);
                if (!string.Equals(cursorSession, session ?? string.Empty, StringComparison.Ordinal))
                {
                    error = "cursor_session_mismatch";
                    return false;
                }
                if (!string.Equals(DecodePart(parts[2]), BridgeText.NormalizeCommand(command), StringComparison.Ordinal))
                {
                    error = "cursor_query_mismatch";
                    return false;
                }
                if (!string.Equals(DecodePart(parts[3]), scope ?? string.Empty, StringComparison.Ordinal))
                {
                    error = "cursor_filter_mismatch";
                    return false;
                }
                if (!string.Equals(DecodePart(parts[4]), ordering ?? string.Empty, StringComparison.Ordinal))
                {
                    error = "cursor_order_mismatch";
                    return false;
                }
                snapshotId = DecodePart(parts[5]);
                if (!Guid.TryParseExact(snapshotId, "N", out _))
                {
                    error = "invalid_cursor";
                    return false;
                }
                if (!long.TryParse(parts[6], System.Globalization.NumberStyles.None,
                    System.Globalization.CultureInfo.InvariantCulture, out expiryTicks) || expiryTicks <= 0)
                    return false;
                if (new DateTime(expiryTicks, DateTimeKind.Utc) <= DateTime.UtcNow)
                {
                    error = "cursor_expired";
                    return false;
                }
                if (!int.TryParse(parts[7], System.Globalization.NumberStyles.None,
                    System.Globalization.CultureInfo.InvariantCulture, out offset) || offset < 0)
                    return false;
                return true;
            }
            catch { return false; }
        }

        private static string EncodePart(string value) =>
            Convert.ToBase64String(Encoding.UTF8.GetBytes(value ?? string.Empty));

        private static string DecodePart(string value) =>
            Encoding.UTF8.GetString(Convert.FromBase64String(value));
    }
}
