using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Linq;
using System.Runtime.Serialization;
using System.Runtime.Serialization.Json;
using System.Security.Cryptography;
using System.Text;
using System.Threading;

namespace RimWorldDevBridge
{
    // Logical agent names are labels. Client credentials and server-owned quota keys are
    // intentionally separate so a caller cannot turn an identity field into authority.
    [DataContract]
    public sealed class BridgeClientIdentity
    {
        [DataMember(Order = 1)] public string AgentId;
        [DataMember(Order = 2)] public string ClientInstanceId;
        [DataMember(Order = 3)] public string ConnectionSessionId;
        [DataMember(Order = 4)] public string RequestCorrelationId;
        [DataMember(Order = 5)] public string ParticipantId;
        [DataMember(Order = 6)] public string SanitizedAgentId;
        [DataMember(Order = 7)] public string SanitizedClientInstanceId;
        [DataMember(Order = 8)] public string SanitizedParticipantId;

        public static BridgeClientIdentity Create(string agentId = null, string clientInstanceId = null,
            string connectionSessionId = null, string requestCorrelationId = null, string participantId = null)
        {
            BridgeClientIdentity value = new BridgeClientIdentity
            {
                AgentId = BridgeIdentityRules.Normalize(agentId, "agent"),
                ClientInstanceId = BridgeIdentityRules.Normalize(clientInstanceId, "client"),
                ConnectionSessionId = BridgeIdentityRules.Normalize(connectionSessionId, "connection"),
                RequestCorrelationId = BridgeIdentityRules.Normalize(requestCorrelationId, "request"),
                ParticipantId = BridgeIdentityRules.Normalize(participantId, "participant")
            };
            value.RefreshSanitizedValues();
            return value;
        }

        public BridgeClientIdentity ForRequest(string correlationId = null, string participantId = null)
        {
            BridgeClientIdentity result = new BridgeClientIdentity
            {
                AgentId = AgentId,
                ClientInstanceId = ClientInstanceId,
                ConnectionSessionId = ConnectionSessionId,
                RequestCorrelationId = BridgeIdentityRules.Normalize(correlationId, "request"),
                ParticipantId = BridgeIdentityRules.Normalize(participantId ?? ParticipantId, "participant")
            };
            result.RefreshSanitizedValues();
            return result;
        }

        public string QuotaSubject => ClientInstanceId + "@" + AgentId;

        public void RefreshSanitizedValues()
        {
            SanitizedAgentId = BridgeIdentityRules.Sanitize(AgentId);
            SanitizedClientInstanceId = BridgeIdentityRules.Sanitize(ClientInstanceId);
            SanitizedParticipantId = BridgeIdentityRules.Sanitize(ParticipantId);
        }
    }

    [DataContract]
    public sealed class BridgeClientRegistration
    {
        [DataMember(Order = 1)] public BridgeClientIdentity Identity;
        [DataMember(Order = 2)] public string ServerQuotaKey;
        [DataMember(Order = 3)] public string Credential;
        [DataMember(Order = 4)] public DateTime CreatedUtc;
        [DataMember(Order = 5)] public DateTime LastSeenUtc;
    }

    // This authority is useful to hosts that can issue a client credential. The game bridge
    // transport token remains the transport credential; these records prevent an identity label
    // from being used as a substitute for an authenticated client binding.
    public sealed class BridgeIdentityAuthority
    {
        private readonly object gate = new object();
        private readonly Dictionary<string, BridgeClientRegistration> registrations =
            new Dictionary<string, BridgeClientRegistration>(StringComparer.Ordinal);
        private string persistencePath;

        public void Load(string path)
        {
            persistencePath = string.IsNullOrWhiteSpace(path) ? null : Path.GetFullPath(path);
            List<BridgeClientRegistration> persisted = BridgeDurableJson.Read<List<BridgeClientRegistration>>(persistencePath);
            lock (gate)
            {
                registrations.Clear();
                foreach (BridgeClientRegistration registration in persisted ?? new List<BridgeClientRegistration>())
                {
                    if (registration?.Identity == null ||
                        !BridgeIdentityRules.IsValid(registration.Identity.AgentId) ||
                        !BridgeIdentityRules.IsValid(registration.Identity.ClientInstanceId) ||
                        string.IsNullOrWhiteSpace(registration.Credential)) continue;
                    registrations[registration.Identity.ClientInstanceId] = Clone(registration);
                }
            }
        }

        public BridgeClientRegistration Register(string agentId, string clientInstanceId = null)
        {
            return Register(agentId, clientInstanceId, null);
        }

        public BridgeClientRegistration Register(string agentId, string clientInstanceId, string credential)
        {
            BridgeClientIdentity identity = BridgeClientIdentity.Create(agentId, clientInstanceId);
            lock (gate)
            {
                if (registrations.ContainsKey(identity.ClientInstanceId))
                    throw new InvalidOperationException("client_instance_already_registered");
                BridgeClientRegistration registration = new BridgeClientRegistration
                {
                    Identity = identity,
                    ServerQuotaKey = "quota-" + Guid.NewGuid().ToString("N"),
                    Credential = string.IsNullOrWhiteSpace(credential) ? Convert.ToBase64String(RandomBytes(32)) :
                        credential,
                    CreatedUtc = DateTime.UtcNow,
                    LastSeenUtc = DateTime.UtcNow
                };
                registrations.Add(identity.ClientInstanceId, registration);
                PersistLocked();
                return Clone(registration);
            }
        }

        public bool Authenticate(BridgeClientIdentity identity, string credential,
            out BridgeClientRegistration registration)
        {
            registration = null;
            if (identity == null || string.IsNullOrWhiteSpace(credential)) return false;
            lock (gate)
            {
                BridgeClientRegistration stored;
                if (!registrations.TryGetValue(identity.ClientInstanceId ?? string.Empty, out stored)) return false;
                if (!FixedEquals(stored.Credential, credential) ||
                    !string.Equals(stored.Identity.AgentId, identity.AgentId, StringComparison.Ordinal)) return false;
                stored.LastSeenUtc = DateTime.UtcNow;
                registration = Clone(stored);
                return true;
            }
        }

        public bool IsRegistered(string clientInstanceId)
        {
            lock (gate) return registrations.ContainsKey(clientInstanceId ?? string.Empty);
        }

        private static byte[] RandomBytes(int length)
        {
            byte[] bytes = new byte[length];
            using (RandomNumberGenerator random = RandomNumberGenerator.Create()) random.GetBytes(bytes);
            return bytes;
        }

        private void PersistLocked()
        {
            if (string.IsNullOrWhiteSpace(persistencePath)) return;
            BridgeDurableJson.WriteAtomic(persistencePath, registrations.Values.Select(Clone).ToList());
        }

        private static bool FixedEquals(string left, string right)
        {
            byte[] first = Encoding.UTF8.GetBytes(left ?? string.Empty);
            byte[] second = Encoding.UTF8.GetBytes(right ?? string.Empty);
            if (first.Length != second.Length) return false;
            int difference = 0;
            for (int index = 0; index < first.Length; index++) difference |= first[index] ^ second[index];
            return difference == 0;
        }

        private static BridgeClientRegistration Clone(BridgeClientRegistration value)
        {
            return new BridgeClientRegistration
            {
                Identity = new BridgeClientIdentity
                {
                    AgentId = value.Identity.AgentId,
                    ClientInstanceId = value.Identity.ClientInstanceId,
                    ConnectionSessionId = value.Identity.ConnectionSessionId,
                    RequestCorrelationId = value.Identity.RequestCorrelationId,
                    ParticipantId = value.Identity.ParticipantId,
                    SanitizedAgentId = value.Identity.SanitizedAgentId,
                    SanitizedClientInstanceId = value.Identity.SanitizedClientInstanceId,
                    SanitizedParticipantId = value.Identity.SanitizedParticipantId
                },
                ServerQuotaKey = value.ServerQuotaKey,
                Credential = value.Credential,
                CreatedUtc = value.CreatedUtc,
                LastSeenUtc = value.LastSeenUtc
            };
        }
    }

    public static class BridgeIdentityRules
    {
        public static string Normalize(string value, string prefix)
        {
            if (string.IsNullOrWhiteSpace(value)) return prefix + "-" + Guid.NewGuid().ToString("N");
            string text = value.Trim();
            StringBuilder result = new StringBuilder(Math.Min(128, text.Length));
            foreach (char character in text)
            {
                if (result.Length >= 128) break;
                result.Append(char.IsLetterOrDigit(character) || character == '.' || character == '_' ||
                    character == '-' || character == ':' ? character : '-');
            }
            string normalized = result.ToString().Trim('-');
            return string.IsNullOrEmpty(normalized) ? prefix + "-" + Guid.NewGuid().ToString("N") : normalized;
        }

        public static string Sanitize(string value)
        {
            if (string.IsNullOrEmpty(value)) return "anonymous";
            using (SHA256 sha = SHA256.Create())
                return "id-" + BitConverter.ToString(sha.ComputeHash(Encoding.UTF8.GetBytes(value)))
                    .Replace("-", string.Empty).Substring(0, 12).ToLowerInvariant();
        }

        public static bool IsValid(string value, int maximum = 128)
        {
            if (string.IsNullOrWhiteSpace(value) || value.Length > maximum) return false;
            return value.All(character => char.IsLetterOrDigit(character) || character == '.' ||
                character == '_' || character == '-' || character == ':');
        }
    }

    public interface IBridgeClock
    {
        DateTime UtcNow { get; }
    }

    public sealed class BridgeSystemClock : IBridgeClock
    {
        public DateTime UtcNow => DateTime.UtcNow;
    }

    public sealed class BridgeManualClock : IBridgeClock
    {
        private DateTime now;
        public BridgeManualClock(DateTime initialUtc) { now = initialUtc.ToUniversalTime(); }
        public DateTime UtcNow => now;
        public void Advance(TimeSpan duration) { now = now.Add(duration); }
        public void Set(DateTime value) { now = value.ToUniversalTime(); }
    }

    public enum BridgeOperationKind
    {
        Activation,
        Restart,
        Readiness,
        SaveLoad,
        AdapterReload,
        Deployment,
        Verification
    }

    public enum BridgeDesiredState
    {
        Bridge,
        Game,
        Map,
        TestReady
    }

    public enum BridgeOperationState
    {
        Queued,
        Running,
        Recovering,
        Succeeded,
        Failed,
        Cancelled,
        Abandoned,
        Pending = 7,
        WaitingForAuthorization = 8,
        WaitingForCapacity = 9,
        Denied = 10,
        TimedOut = 11
    }

    public enum BridgeParticipationState
    {
        Attached,
        Detached,
        Cancelled
    }

    public enum BridgeAbandonmentPolicy
    {
        CompleteSafeWork,
        CancelSafely,
        LeaveRuntimeRunning
    }

    [DataContract]
    public sealed class BridgeOperationCompatibilityKey : IEquatable<BridgeOperationCompatibilityKey>
    {
        [DataMember(Order = 1)] public string OperationKind;
        [DataMember(Order = 2)] public string DesiredState;
        [DataMember(Order = 3)] public string ManagedProfile;
        [DataMember(Order = 4)] public string RimWorldVersion;
        [DataMember(Order = 5)] public string ModSetFingerprint;
        [DataMember(Order = 6)] public string ModLoadOrderFingerprint;
        [DataMember(Order = 7)] public string SourceBuildIdentity;
        [DataMember(Order = 8)] public string DeploymentSlot;
        [DataMember(Order = 9)] public string ExpectedCoreFingerprint;
        [DataMember(Order = 10)] public string ExpectedAdapterFingerprint;
        [DataMember(Order = 11)] public string LoadedAssemblyFingerprint;
        [DataMember(Order = 12)] public string DeploymentId;
        [DataMember(Order = 13)] public string ArtifactFingerprint;
        [DataMember(Order = 14)] public string ConfigurationFingerprint;
        [DataMember(Order = 15)] public string UserRootFingerprint;
        [DataMember(Order = 16)] public string SaveTarget;
        [DataMember(Order = 17)] public string MapTarget;
        [DataMember(Order = 18)] public bool RequiresProcessReplacement;
        [DataMember(Order = 19)] public long LifecycleGeneration;
        [DataMember(Order = 20)] public string MutationScope;
        [DataMember(Order = 21)] public string CanonicalValue;
        [DataMember(Order = 22)] public string Digest;

        public static BridgeOperationCompatibilityKey Create(BridgeOperationKind kind,
            BridgeDesiredState desiredState, string managedProfile, string rimWorldVersion,
            string modSetFingerprint, string modLoadOrderFingerprint, string sourceBuildIdentity,
            string deploymentSlot, string expectedCoreFingerprint, string expectedAdapterFingerprint,
            string configurationFingerprint, string userRootFingerprint, string saveTarget,
            string mapTarget, bool requiresProcessReplacement, long lifecycleGeneration,
            string mutationScope, string deploymentId = null, string artifactFingerprint = null,
            string loadedAssemblyFingerprint = null)
        {
            BridgeOperationCompatibilityKey key = new BridgeOperationCompatibilityKey
            {
                OperationKind = kind.ToString(),
                DesiredState = desiredState.ToString(),
                ManagedProfile = Clean(managedProfile),
                RimWorldVersion = Clean(rimWorldVersion),
                ModSetFingerprint = Clean(modSetFingerprint),
                ModLoadOrderFingerprint = Clean(modLoadOrderFingerprint),
                SourceBuildIdentity = Clean(sourceBuildIdentity),
                DeploymentSlot = Clean(deploymentSlot),
                ExpectedCoreFingerprint = Clean(expectedCoreFingerprint),
                ExpectedAdapterFingerprint = Clean(expectedAdapterFingerprint),
                LoadedAssemblyFingerprint = Clean(loadedAssemblyFingerprint),
                DeploymentId = Clean(deploymentId),
                ArtifactFingerprint = Clean(artifactFingerprint),
                ConfigurationFingerprint = Clean(configurationFingerprint),
                UserRootFingerprint = Clean(userRootFingerprint),
                SaveTarget = Clean(saveTarget),
                MapTarget = Clean(mapTarget),
                RequiresProcessReplacement = requiresProcessReplacement,
                LifecycleGeneration = lifecycleGeneration,
                MutationScope = Clean(mutationScope)
            };
            key.CanonicalValue = key.BuildCanonicalValue();
            key.Digest = BridgeHashing.Sha256(key.CanonicalValue);
            return key;
        }

        public static BridgeOperationCompatibilityKey FromDigest(string value)
        {
            string digest = value ?? string.Empty;
            if (digest.StartsWith("compat-v1-", StringComparison.Ordinal))
                digest = digest.Substring("compat-v1-".Length);
            if (!IsSha256(digest)) throw new ArgumentException("compatibility_digest_invalid");
            return new BridgeOperationCompatibilityKey { Digest = digest.ToUpperInvariant() };
        }

        public bool Equals(BridgeOperationCompatibilityKey other)
        {
            return other != null && string.Equals(StableDigest, other.StableDigest, StringComparison.Ordinal);
        }

        public override bool Equals(object obj) => Equals(obj as BridgeOperationCompatibilityKey);
        public override int GetHashCode() => StableDigest.GetHashCode();
        public override string ToString() => "compat-v1-" + StableDigest;

        private string StableDigest
        {
            get
            {
                if (HasStructuredFields) return BridgeHashing.Sha256(BuildCanonicalValue());
                if (!string.IsNullOrWhiteSpace(CanonicalValue)) return BridgeHashing.Sha256(CanonicalValue);
                if (!IsSha256(Digest)) throw new ArgumentException("compatibility_digest_invalid");
                return Digest.ToUpperInvariant();
            }
        }

        private bool HasStructuredFields => !string.IsNullOrEmpty(OperationKind) ||
            !string.IsNullOrEmpty(DesiredState) || !string.IsNullOrEmpty(ManagedProfile) ||
            !string.IsNullOrEmpty(RimWorldVersion) || !string.IsNullOrEmpty(ModSetFingerprint) ||
            !string.IsNullOrEmpty(ModLoadOrderFingerprint) || !string.IsNullOrEmpty(SourceBuildIdentity) ||
            !string.IsNullOrEmpty(DeploymentSlot) || !string.IsNullOrEmpty(ExpectedCoreFingerprint) ||
            !string.IsNullOrEmpty(ExpectedAdapterFingerprint) || !string.IsNullOrEmpty(LoadedAssemblyFingerprint) ||
            !string.IsNullOrEmpty(DeploymentId) || !string.IsNullOrEmpty(ArtifactFingerprint) ||
            !string.IsNullOrEmpty(ConfigurationFingerprint) ||
            !string.IsNullOrEmpty(UserRootFingerprint) || !string.IsNullOrEmpty(SaveTarget) ||
            !string.IsNullOrEmpty(MapTarget) || RequiresProcessReplacement || LifecycleGeneration != 0 ||
            !string.IsNullOrEmpty(MutationScope);

        private static bool IsSha256(string value)
        {
            return !string.IsNullOrWhiteSpace(value) && value.Length == 64 &&
                value.All(character => (character >= '0' && character <= '9') ||
                    (character >= 'a' && character <= 'f') || (character >= 'A' && character <= 'F'));
        }

        private string BuildCanonicalValue()
        {
            StringBuilder value = new StringBuilder();
            Append(value, "operationKind", OperationKind);
            Append(value, "desiredState", DesiredState);
            Append(value, "managedProfile", ManagedProfile);
            Append(value, "rimWorldVersion", RimWorldVersion);
            Append(value, "modSetFingerprint", ModSetFingerprint);
            Append(value, "modLoadOrderFingerprint", ModLoadOrderFingerprint);
            Append(value, "sourceBuildIdentity", SourceBuildIdentity);
            Append(value, "deploymentSlot", DeploymentSlot);
            Append(value, "expectedCoreFingerprint", ExpectedCoreFingerprint);
            Append(value, "expectedAdapterFingerprint", ExpectedAdapterFingerprint);
            Append(value, "loadedAssemblyFingerprint", LoadedAssemblyFingerprint);
            Append(value, "deploymentId", DeploymentId);
            Append(value, "artifactFingerprint", ArtifactFingerprint);
            Append(value, "configurationFingerprint", ConfigurationFingerprint);
            Append(value, "userRootFingerprint", UserRootFingerprint);
            Append(value, "saveTarget", SaveTarget);
            Append(value, "mapTarget", MapTarget);
            Append(value, "requiresProcessReplacement", RequiresProcessReplacement ? "true" : "false");
            Append(value, "lifecycleGeneration", LifecycleGeneration.ToString(CultureInfo.InvariantCulture));
            Append(value, "mutationScope", MutationScope);
            return value.ToString();
        }

        private static void Append(StringBuilder target, string name, string value)
        {
            string item = value ?? string.Empty;
            target.Append(name).Append(':').Append(item.Length.ToString(CultureInfo.InvariantCulture))
                .Append(':').Append(item).Append('|');
        }

        private static string Clean(string value) => (value ?? string.Empty).Trim();
    }

    [DataContract]
    public sealed class BridgeProcessIdentity
    {
        [DataMember(Order = 1)] public int Pid;
        [DataMember(Order = 2)] public string ProcessStartIdentity;
        [DataMember(Order = 3)] public string SessionId;
        [DataMember(Order = 4)] public long LifecycleGeneration;
        [DataMember(Order = 5)] public string RuntimeSlotId;
        [DataMember(Order = 6)] public string LoadedAssemblyFingerprint;
        [DataMember(Order = 7)] public string ExecutablePath;
        [DataMember(Order = 8)] public string ProfileFingerprint;

        public string StableValue => string.Join("|", Pid.ToString(CultureInfo.InvariantCulture),
            ProcessStartIdentity ?? string.Empty, SessionId ?? string.Empty,
            LifecycleGeneration.ToString(CultureInfo.InvariantCulture), RuntimeSlotId ?? string.Empty,
            LoadedAssemblyFingerprint ?? string.Empty, ExecutablePath ?? string.Empty,
            ProfileFingerprint ?? string.Empty);
    }

    [DataContract]
    public sealed class BridgeRuntimeObservation
    {
        [DataMember(Order = 1)] public string RuntimeSlotId;
        [DataMember(Order = 2)] public string OperationId;
        [DataMember(Order = 3)] public BridgeProcessIdentity Process;
        [DataMember(Order = 4)] public long ProgressSequence;
        [DataMember(Order = 5)] public DateTime LastProgressAtUtc;
        [DataMember(Order = 6)] public bool Terminal;
    }

    public static class BridgeObservationGuard
    {
        public static bool Accept(BridgeRuntimeObservation current, BridgeRuntimeObservation candidate)
        {
            if (candidate == null || candidate.Process == null) return false;
            if (current == null) return true;
            if (!string.Equals(current.RuntimeSlotId, candidate.RuntimeSlotId, StringComparison.Ordinal) ||
                !string.Equals(current.OperationId, candidate.OperationId, StringComparison.Ordinal)) return false;
            if (!SameProcess(current.Process, candidate.Process)) return false;
            return candidate.ProgressSequence > current.ProgressSequence ||
                candidate.Terminal && !current.Terminal && candidate.ProgressSequence >= current.ProgressSequence;
        }

        private static bool SameProcess(BridgeProcessIdentity first, BridgeProcessIdentity second)
        {
            return first.Pid == second.Pid &&
                string.Equals(first.ProcessStartIdentity, second.ProcessStartIdentity, StringComparison.Ordinal) &&
                string.Equals(first.SessionId, second.SessionId, StringComparison.Ordinal) &&
                first.LifecycleGeneration == second.LifecycleGeneration &&
                string.Equals(first.RuntimeSlotId, second.RuntimeSlotId, StringComparison.Ordinal) &&
                string.Equals(first.LoadedAssemblyFingerprint, second.LoadedAssemblyFingerprint,
                    StringComparison.Ordinal);
        }
    }

    public static class BridgeHashing
    {
        public static string Sha256(string value)
        {
            using (SHA256 sha = SHA256.Create())
                return string.Concat(sha.ComputeHash(Encoding.UTF8.GetBytes(value ?? string.Empty))
                    .Select(item => item.ToString("X2", CultureInfo.InvariantCulture)));
        }

        public static string FileSha256(string path)
        {
            using (SHA256 sha = SHA256.Create())
            using (FileStream stream = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.Read))
                return string.Concat(sha.ComputeHash(stream).Select(item => item.ToString("X2",
                    CultureInfo.InvariantCulture)));
        }

        public static string StablePathFingerprint(IEnumerable<string> paths)
        {
            StringBuilder canonical = new StringBuilder();
            foreach (string path in (paths ?? Enumerable.Empty<string>()).OrderBy(item => item,
                StringComparer.OrdinalIgnoreCase))
                canonical.Append(path ?? string.Empty).Append('\n');
            return Sha256(canonical.ToString());
        }
    }

    internal static class BridgeDurableJson
    {
        internal static IDisposable AcquireStateLock(string path)
        {
            if (string.IsNullOrWhiteSpace(path)) return new NoopDisposable();
            string name = "Local\\RimWorldDevBridge.State." + BridgeHashing.Sha256(Path.GetFullPath(path));
            Mutex mutex = new Mutex(false, name);
            try
            {
                if (!mutex.WaitOne(TimeSpan.FromSeconds(10)))
                {
                    mutex.Dispose();
                    throw new IOException("durable_state_lock_timeout");
                }
                return new MutexLease(mutex);
            }
            catch
            {
                mutex.Dispose();
                throw;
            }
        }

        internal static void WriteAtomic<T>(string path, T value)
        {
            using (AcquireStateLock(path)) WriteAtomicUnlocked(path, value);
        }

        private static void WriteAtomicUnlocked<T>(string path, T value)
        {
            string directory = Path.GetDirectoryName(path);
            if (!string.IsNullOrEmpty(directory)) Directory.CreateDirectory(directory);
            IOException failure = null;
            for (int attempt = 0; attempt < 8; attempt++)
            {
                string temporary = path + ".tmp." + Guid.NewGuid().ToString("N");
                try
                {
                    using (FileStream ownership = new FileStream(path + ".lock", FileMode.OpenOrCreate,
                        FileAccess.ReadWrite, FileShare.None))
                    {
                        using (FileStream stream = new FileStream(temporary, FileMode.CreateNew, FileAccess.Write,
                            FileShare.None))
                            new DataContractJsonSerializer(typeof(T)).WriteObject(stream, value);
                        if (File.Exists(path)) File.Replace(temporary, path, null);
                        else File.Move(temporary, path);
                    }
                    return;
                }
                catch (IOException exception)
                {
                    failure = exception;
                    if (attempt < 7) Thread.Sleep(25 * (attempt + 1));
                }
                finally
                {
                    if (File.Exists(temporary)) File.Delete(temporary);
                }
            }
            throw failure ?? new IOException("durable_state_write_failed");
        }

        internal static T Read<T>(string path) where T : class
        {
            if (!File.Exists(path)) return null;
            using (AcquireStateLock(path))
            using (FileStream stream = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.Read))
                return new DataContractJsonSerializer(typeof(T)).ReadObject(stream) as T;
        }

        private sealed class MutexLease : IDisposable
        {
            private Mutex mutex;
            internal MutexLease(Mutex mutex) { this.mutex = mutex; }
            public void Dispose()
            {
                if (mutex == null) return;
                try { mutex.ReleaseMutex(); } catch { }
                mutex.Dispose();
                mutex = null;
            }
        }

        private sealed class NoopDisposable : IDisposable
        {
            public void Dispose() { }
        }
    }
}
