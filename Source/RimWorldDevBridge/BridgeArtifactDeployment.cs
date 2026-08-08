using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Reflection;
using System.Runtime.Serialization;

namespace RimWorldDevBridge
{
    [DataContract]
    public sealed class BridgeAssemblyMetadata
    {
        [DataMember(Order = 1)] public string AssemblyName;
        [DataMember(Order = 2)] public string AssemblyVersion;
        [DataMember(Order = 3)] public string ModuleVersionId;
        [DataMember(Order = 4)] public string Sha256;
        [DataMember(Order = 5)] public string RelativePath;
    }

    [DataContract]
    public sealed class BridgeArtifactIdentity
    {
        [DataMember(Order = 1)] public string ArtifactFingerprint;
        [DataMember(Order = 2)] public string SourceRevision;
        [DataMember(Order = 3)] public string DirtySourceFingerprint;
        [DataMember(Order = 4)] public string BuildConfiguration;
        [DataMember(Order = 5)] public string TargetFramework;
        [DataMember(Order = 6)] public string DependencyFingerprint;
        [DataMember(Order = 7)] public string ModFingerprint;
        [DataMember(Order = 8)] public string ModLoadOrderFingerprint;
        [DataMember(Order = 9)] public string DeploymentManifestFingerprint;
        [DataMember(Order = 10)] public string DeploymentSlot;
        [DataMember(Order = 11)] public string LoadedAssemblyFingerprint;
        [DataMember(Order = 12)] public string ProducingOperationId;
        [DataMember(Order = 13)] public string ProducingAgentId;
        [DataMember(Order = 14)] public DateTime ProducedAtUtc;
        [DataMember(Order = 15)] public string Provenance;
        [DataMember(Order = 16)] public Dictionary<string, string> OutputArtifactHashes =
            new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
        [DataMember(Order = 17)] public List<BridgeAssemblyMetadata> Assemblies =
            new List<BridgeAssemblyMetadata>();

        public static BridgeArtifactIdentity FromFiles(IEnumerable<string> files, string sourceRevision,
            string dirtySourceFingerprint, string buildConfiguration, string targetFramework,
            string dependencyFingerprint, string modFingerprint, string modLoadOrderFingerprint,
            string deploymentSlot, string producingOperationId, string producingAgentId,
            string provenance = null)
        {
            BridgeArtifactIdentity artifact = new BridgeArtifactIdentity
            {
                SourceRevision = sourceRevision ?? string.Empty,
                DirtySourceFingerprint = dirtySourceFingerprint ?? string.Empty,
                BuildConfiguration = buildConfiguration ?? string.Empty,
                TargetFramework = targetFramework ?? string.Empty,
                DependencyFingerprint = dependencyFingerprint ?? string.Empty,
                ModFingerprint = modFingerprint ?? string.Empty,
                ModLoadOrderFingerprint = modLoadOrderFingerprint ?? string.Empty,
                DeploymentSlot = deploymentSlot ?? string.Empty,
                ProducingOperationId = producingOperationId ?? string.Empty,
                ProducingAgentId = producingAgentId ?? string.Empty,
                ProducedAtUtc = DateTime.UtcNow,
                Provenance = provenance ?? string.Empty
            };
            foreach (string file in (files ?? Enumerable.Empty<string>()).OrderBy(item => item,
                StringComparer.OrdinalIgnoreCase))
            {
                if (!File.Exists(file)) throw new FileNotFoundException("artifact_file_missing", file);
                string relative = Path.GetFileName(file);
                string hash = BridgeHashing.FileSha256(file);
                if (artifact.OutputArtifactHashes.ContainsKey(relative))
                    throw new InvalidDataException("artifact_duplicate_output");
                artifact.OutputArtifactHashes[relative] = hash;
                BridgeAssemblyMetadata assembly = AssemblyMetadata(file, hash, relative);
                if (assembly != null) artifact.Assemblies.Add(assembly);
            }
            artifact.LoadedAssemblyFingerprint = ComputeLoadedAssemblyFingerprint(artifact.Assemblies,
                artifact.OutputArtifactHashes);
            artifact.ArtifactFingerprint = artifact.ComputeFingerprint();
            return artifact;
        }

        public bool MatchesSourceFiles(IDictionary<string, string> sourceFiles)
        {
            if (sourceFiles == null || sourceFiles.Count != OutputArtifactHashes.Count) return false;
            Dictionary<string, string> actualHashes = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
            List<BridgeAssemblyMetadata> actualAssemblies = new List<BridgeAssemblyMetadata>();
            foreach (KeyValuePair<string, string> file in sourceFiles)
            {
                string relative = NormalizeArtifactPath(file.Key);
                if (string.IsNullOrEmpty(relative) || actualHashes.ContainsKey(relative) ||
                    !File.Exists(file.Value)) return false;
                string hash = BridgeHashing.FileSha256(file.Value);
                actualHashes[relative] = hash;
                BridgeAssemblyMetadata assembly = AssemblyMetadata(file.Value, hash, relative);
                if (assembly != null) actualAssemblies.Add(assembly);
            }
            if (!MapsEqual(OutputArtifactHashes, actualHashes)) return false;
            string loaded = ComputeLoadedAssemblyFingerprint(actualAssemblies, actualHashes);
            if (!string.Equals(LoadedAssemblyFingerprint, loaded, StringComparison.OrdinalIgnoreCase)) return false;
            BridgeArtifactIdentity actual = new BridgeArtifactIdentity
            {
                SourceRevision = SourceRevision,
                DirtySourceFingerprint = DirtySourceFingerprint,
                BuildConfiguration = BuildConfiguration,
                TargetFramework = TargetFramework,
                DependencyFingerprint = DependencyFingerprint,
                ModFingerprint = ModFingerprint,
                ModLoadOrderFingerprint = ModLoadOrderFingerprint,
                DeploymentSlot = DeploymentSlot,
                ProducingOperationId = ProducingOperationId,
                ProducingAgentId = ProducingAgentId,
                LoadedAssemblyFingerprint = loaded,
                Provenance = Provenance,
                OutputArtifactHashes = actualHashes,
                Assemblies = actualAssemblies
            };
            return string.Equals(ArtifactFingerprint, actual.ComputeFingerprint(),
                StringComparison.OrdinalIgnoreCase);
        }

        public static string ComputeLoadedAssemblyFingerprint(IEnumerable<BridgeAssemblyMetadata> assemblies,
            IDictionary<string, string> outputHashes)
        {
            List<BridgeAssemblyMetadata> values = (assemblies ?? Enumerable.Empty<BridgeAssemblyMetadata>())
                .Where(item => item != null).OrderBy(item => item.RelativePath ?? string.Empty,
                    StringComparer.OrdinalIgnoreCase).ToList();
            if (values.Count > 0)
                return BridgeHashing.Sha256(string.Join("\n", values.Select(item => string.Join("|",
                    item.AssemblyName, item.AssemblyVersion, item.ModuleVersionId, item.Sha256,
                    item.RelativePath))));
            return BridgeHashing.Sha256("no-assemblies\n" + string.Join("\n",
                (outputHashes ?? new Dictionary<string, string>()).OrderBy(item => item.Key,
                    StringComparer.OrdinalIgnoreCase).Select(item => NormalizeArtifactPath(item.Key) + "=" + item.Value)));
        }

        public string ComputeFingerprint()
        {
            List<string> parts = new List<string>
            {
                SourceRevision ?? string.Empty,
                DirtySourceFingerprint ?? string.Empty,
                BuildConfiguration ?? string.Empty,
                TargetFramework ?? string.Empty,
                DependencyFingerprint ?? string.Empty,
                ModFingerprint ?? string.Empty,
                ModLoadOrderFingerprint ?? string.Empty,
                DeploymentSlot ?? string.Empty,
                ProducingOperationId ?? string.Empty,
                ProducingAgentId ?? string.Empty,
                LoadedAssemblyFingerprint ?? string.Empty,
                Provenance ?? string.Empty
            };
            parts.AddRange(OutputArtifactHashes.OrderBy(item => item.Key, StringComparer.OrdinalIgnoreCase)
                .Select(item => item.Key + "=" + item.Value));
            parts.AddRange(Assemblies.OrderBy(item => item.RelativePath ?? string.Empty,
                StringComparer.OrdinalIgnoreCase).Select(item => string.Join("|", item.AssemblyName,
                item.AssemblyVersion, item.ModuleVersionId, item.Sha256, item.RelativePath)));
            return BridgeHashing.Sha256(string.Join("\n", parts));
        }

        public bool Matches(BridgeArtifactIdentity other)
        {
            return other != null && string.Equals(ArtifactFingerprint, other.ArtifactFingerprint,
                StringComparison.OrdinalIgnoreCase) && MapsEqual(OutputArtifactHashes, other.OutputArtifactHashes);
        }

        private static bool MapsEqual(IDictionary<string, string> first, IDictionary<string, string> second)
        {
            if (first == null || second == null || first.Count != second.Count) return false;
            foreach (KeyValuePair<string, string> item in first)
            {
                string value;
                string normalized = NormalizeArtifactPath(item.Key);
                if (!second.TryGetValue(normalized, out value) || !string.Equals(item.Value, value,
                    StringComparison.OrdinalIgnoreCase)) return false;
            }
            return true;
        }

        public static string NormalizeArtifactPath(string value)
        {
            return (value ?? string.Empty).Replace('\\', '/').Trim('/');
        }

        private static BridgeAssemblyMetadata AssemblyMetadata(string path, string hash, string relative)
        {
            try
            {
                AssemblyName identity = AssemblyName.GetAssemblyName(path);
                Assembly loaded = Assembly.ReflectionOnlyLoadFrom(path);
                return new BridgeAssemblyMetadata
                {
                    AssemblyName = identity.Name,
                    AssemblyVersion = identity.Version == null ? string.Empty : identity.Version.ToString(),
                    ModuleVersionId = loaded.ManifestModule.ModuleVersionId.ToString("N"),
                    Sha256 = hash,
                    RelativePath = relative
                };
            }
            catch (BadImageFormatException) { return null; }
            catch (FileLoadException) { return null; }
            catch (FileNotFoundException) { return null; }
        }
    }

    [DataContract]
    public sealed class BridgeDeploymentManifest
    {
        [DataMember(Order = 1)] public string DeploymentId;
        [DataMember(Order = 2)] public string ArtifactFingerprint;
        [DataMember(Order = 3)] public string DeploymentSlot;
        [DataMember(Order = 4)] public string SourceRevision;
        [DataMember(Order = 5)] public string ProducingOperationId;
        [DataMember(Order = 6)] public string ProducingAgentId;
        [DataMember(Order = 7)] public string ManifestFingerprint;
        [DataMember(Order = 8)] public DateTime DeployedAtUtc;
        [DataMember(Order = 9)] public Dictionary<string, string> OutputArtifactHashes =
            new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
        [DataMember(Order = 10)] public List<BridgeAssemblyMetadata> Assemblies =
            new List<BridgeAssemblyMetadata>();
        [DataMember(Order = 11)] public string LoadedAssemblyFingerprint;
        [DataMember(Order = 12)] public string Provenance;

        public static BridgeDeploymentManifest FromArtifact(string deploymentId,
            BridgeArtifactIdentity artifact, string producingOperationId, string producingAgentId,
            DateTime deployedAtUtc)
        {
            if (artifact == null) throw new ArgumentNullException(nameof(artifact));
            BridgeDeploymentManifest manifest = new BridgeDeploymentManifest
            {
                DeploymentId = deploymentId,
                ArtifactFingerprint = artifact.ArtifactFingerprint,
                DeploymentSlot = artifact.DeploymentSlot,
                SourceRevision = artifact.SourceRevision,
                ProducingOperationId = producingOperationId ?? artifact.ProducingOperationId,
                ProducingAgentId = producingAgentId ?? artifact.ProducingAgentId,
                DeployedAtUtc = deployedAtUtc,
                LoadedAssemblyFingerprint = artifact.LoadedAssemblyFingerprint,
                Provenance = artifact.Provenance,
                OutputArtifactHashes = new Dictionary<string, string>(artifact.OutputArtifactHashes,
                    StringComparer.OrdinalIgnoreCase),
                Assemblies = artifact.Assemblies.ToList()
            };
            manifest.ManifestFingerprint = BridgeHashing.Sha256(string.Join("\n", new[]
            {
                manifest.DeploymentId ?? string.Empty,
                manifest.ArtifactFingerprint ?? string.Empty,
                manifest.DeploymentSlot ?? string.Empty,
                manifest.SourceRevision ?? string.Empty,
                manifest.ProducingOperationId ?? string.Empty,
                manifest.ProducingAgentId ?? string.Empty,
                manifest.Provenance ?? string.Empty,
                manifest.LoadedAssemblyFingerprint ?? string.Empty,
                string.Join(";", manifest.OutputArtifactHashes.OrderBy(item => item.Key,
                    StringComparer.OrdinalIgnoreCase).Select(item => item.Key + "=" + item.Value)),
                string.Join(";", manifest.Assemblies.OrderBy(item => item.RelativePath,
                    StringComparer.OrdinalIgnoreCase).Select(item => string.Join("|", item.AssemblyName,
                    item.AssemblyVersion, item.ModuleVersionId, item.Sha256, item.RelativePath)))
            }));
            artifact.DeploymentManifestFingerprint = manifest.ManifestFingerprint;
            return manifest;
        }
    }

    [DataContract]
    public sealed class BridgeDeploymentLockRecord
    {
        [DataMember(Order = 1)] public string LockId;
        [DataMember(Order = 2)] public string DeploymentSlot;
        [DataMember(Order = 3)] public string DeploymentId;
        [DataMember(Order = 4)] public string OperationId;
        [DataMember(Order = 5)] public string AgentId;
        [DataMember(Order = 6)] public string ClientInstanceId;
        [DataMember(Order = 7)] public DateTime AcquiredAtUtc;
        [DataMember(Order = 8)] public DateTime ExpiresAtUtc;
    }

    public sealed class BridgeDeploymentLockResult
    {
        public bool Acquired;
        public string CapacityState;
        public string NextAction;
        public BridgeDeploymentLockRecord Lock;
    }

    public sealed class BridgeDeploymentManager
    {
        private readonly object gate = new object();
        private readonly string root;
        private readonly IBridgeClock clock;
        private readonly Dictionary<string, BridgeDeploymentLockRecord> locks =
            new Dictionary<string, BridgeDeploymentLockRecord>(StringComparer.OrdinalIgnoreCase);
        private readonly Dictionary<string, FileStream> lockHandles =
            new Dictionary<string, FileStream>(StringComparer.OrdinalIgnoreCase);

        public BridgeDeploymentManager(string root, IBridgeClock clock = null)
        {
            this.root = Path.GetFullPath(root);
            this.clock = clock ?? new BridgeSystemClock();
            Directory.CreateDirectory(this.root);
            LoadLocks();
        }

        public BridgeDeploymentLockResult TryAcquireLock(string deploymentSlot, string deploymentId,
            string operationId, string agentId, string clientInstanceId, TimeSpan duration)
        {
            string slot = SafePath(deploymentSlot);
            string id = SafePath(deploymentId);
            if (!BridgeIdentityRules.IsValid(operationId) || !BridgeIdentityRules.IsValid(agentId) ||
                !BridgeIdentityRules.IsValid(clientInstanceId)) throw new ArgumentException("deployment_identity_invalid");
            lock (gate)
            {
                RemoveStaleLocksLocked();
                BridgeDeploymentLockRecord existing;
                if (locks.TryGetValue(slot, out existing) &&
                    (!string.Equals(existing.OperationId, operationId, StringComparison.Ordinal) ||
                     !string.Equals(existing.AgentId, agentId, StringComparison.Ordinal) ||
                     !string.Equals(existing.ClientInstanceId, clientInstanceId, StringComparison.Ordinal) ||
                     !string.Equals(existing.DeploymentId, id, StringComparison.Ordinal)))
                    return new BridgeDeploymentLockResult
                    {
                        Acquired = false,
                        CapacityState = string.Equals(existing.OperationId, operationId, StringComparison.Ordinal) ?
                            "deployment_lock_owner_mismatch" : "deployment_locked",
                        NextAction = "wait without holding a runtime or mutation lease"
                     };
                if (!lockHandles.ContainsKey(slot))
                {
                    try
                    {
                        string lockRoot = Path.Combine(root, ".locks");
                        Directory.CreateDirectory(lockRoot);
                        FileStream handle = new FileStream(Path.Combine(lockRoot, slot + ".lock"),
                            FileMode.OpenOrCreate, FileAccess.ReadWrite, FileShare.None);
                        lockHandles[slot] = handle;
                    }
                    catch (IOException)
                    {
                        return new BridgeDeploymentLockResult
                        {
                            Acquired = false,
                            CapacityState = "deployment_locked",
                            NextAction = "wait without holding a runtime or mutation lease"
                        };
                    }
                }
                BridgeDeploymentLockRecord value = existing ?? new BridgeDeploymentLockRecord
                {
                    LockId = "deploy-lock-" + Guid.NewGuid().ToString("N"),
                    DeploymentSlot = slot,
                    DeploymentId = id,
                    OperationId = operationId ?? string.Empty,
                    AgentId = agentId ?? string.Empty,
                    ClientInstanceId = clientInstanceId ?? string.Empty,
                    AcquiredAtUtc = clock.UtcNow
                };
                value.ExpiresAtUtc = clock.UtcNow.Add(duration <= TimeSpan.Zero ? TimeSpan.FromMinutes(2) : duration);
                locks[value.DeploymentSlot] = value;
                PersistLocksLocked();
                return new BridgeDeploymentLockResult { Acquired = true, CapacityState = "admitted",
                    NextAction = "deploy atomically and release the scoped deployment lock", Lock = Clone(value) };
            }
        }

        public void ReleaseLock(string lockId, string operationId, string agentId, string clientInstanceId)
        {
            lock (gate)
            {
                string slot = locks.Where(item => string.Equals(item.Value.LockId, lockId,
                     StringComparison.Ordinal) && string.Equals(item.Value.OperationId, operationId,
                     StringComparison.Ordinal) && string.Equals(item.Value.AgentId, agentId,
                     StringComparison.Ordinal) && string.Equals(item.Value.ClientInstanceId, clientInstanceId,
                     StringComparison.Ordinal)).Select(item => item.Key).FirstOrDefault();
                if (slot != null)
                {
                    locks.Remove(slot);
                    ReleaseHandleLocked(slot);
                    PersistLocksLocked();
                }
            }
        }

        private void RenewLock(string lockId, string operationId, string agentId, string clientInstanceId,
            TimeSpan duration)
        {
            lock (gate)
            {
                BridgeDeploymentLockRecord record = locks.Values.FirstOrDefault(item =>
                    string.Equals(item.LockId, lockId, StringComparison.Ordinal) &&
                    string.Equals(item.OperationId, operationId, StringComparison.Ordinal) &&
                    string.Equals(item.AgentId, agentId, StringComparison.Ordinal) &&
                    string.Equals(item.ClientInstanceId, clientInstanceId, StringComparison.Ordinal));
                if (record == null) throw new InvalidOperationException("deployment_lock_owner_mismatch");
                record.ExpiresAtUtc = clock.UtcNow.Add(duration <= TimeSpan.Zero ? TimeSpan.FromMinutes(5) : duration);
                PersistLocksLocked();
            }
        }

        public int ReleaseStaleLocks()
        {
            lock (gate)
            {
                int count = RemoveStaleLocksLocked();
                if (count > 0) PersistLocksLocked();
                return count;
            }
        }

        public BridgeDeploymentManifest Deploy(BridgeArtifactIdentity artifact,
            IDictionary<string, string> sourceFiles, string deploymentId, string operationId,
            string agentId, string clientInstanceId, TimeSpan lockDuration)
        {
            if (artifact == null || string.IsNullOrWhiteSpace(deploymentId))
                throw new ArgumentException("deployment_input_invalid");
            if (!string.IsNullOrWhiteSpace(artifact.ProducingAgentId) &&
                !string.Equals(artifact.ProducingAgentId, agentId, StringComparison.Ordinal))
                throw new InvalidOperationException("artifact_agent_mismatch");
            if (!string.IsNullOrWhiteSpace(artifact.ProducingOperationId) &&
                !string.Equals(artifact.ProducingOperationId, operationId, StringComparison.Ordinal))
                throw new InvalidOperationException("artifact_operation_mismatch");
            IDictionary<string, string> inputs = sourceFiles ??
                new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
            if (!artifact.MatchesSourceFiles(inputs))
                throw new InvalidDataException("deployment_artifact_fingerprint_mismatch");
            BridgeDeploymentLockResult acquired = TryAcquireLock(artifact.DeploymentSlot, deploymentId,
                operationId, agentId, clientInstanceId, lockDuration);
            if (!acquired.Acquired) throw new InvalidOperationException(acquired.CapacityState);
            string slotRoot = Path.Combine(root, SafePath(artifact.DeploymentSlot));
            string safeDeploymentId = SafePath(deploymentId);
            string staging = Path.Combine(slotRoot, ".staging", safeDeploymentId + "." + Guid.NewGuid().ToString("N"));
            string final = Path.Combine(slotRoot, "deployments", safeDeploymentId);
            try
            {
                Directory.CreateDirectory(staging);
                foreach (KeyValuePair<string, string> file in inputs)
                {
                    RenewLock(acquired.Lock.LockId, operationId, agentId, clientInstanceId, lockDuration);
                    string relative = SafeRelativePath(file.Key);
                    if (!File.Exists(file.Value)) throw new FileNotFoundException("deployment_source_missing", file.Value);
                    string target = Path.Combine(staging, relative);
                    string directory = Path.GetDirectoryName(target);
                    if (!string.IsNullOrEmpty(directory)) Directory.CreateDirectory(directory);
                    string temporary = target + ".tmp." + Guid.NewGuid().ToString("N");
                    File.Copy(file.Value, temporary, false);
                    if (!string.Equals(BridgeHashing.FileSha256(temporary),
                        ExpectedHash(artifact, relative), StringComparison.OrdinalIgnoreCase))
                        throw new InvalidDataException("deployment_artifact_fingerprint_mismatch");
                    File.Move(temporary, target);
                }
                RenewLock(acquired.Lock.LockId, operationId, agentId, clientInstanceId, lockDuration);
                Directory.CreateDirectory(Path.GetDirectoryName(final));
                if (Directory.Exists(final)) throw new IOException("deployment_id_already_exists");
                BridgeDeploymentManifest manifest = BridgeDeploymentManifest.FromArtifact(deploymentId, artifact,
                    operationId, agentId, clock.UtcNow);
                BridgeDurableJson.WriteAtomic(Path.Combine(staging, "deployment.manifest.json"), manifest);
                Directory.Move(staging, final);
                BridgeDurableJson.WriteAtomic(Path.Combine(slotRoot, "current.json"), manifest);
                return manifest;
            }
            finally
            {
                if (Directory.Exists(staging)) TryDelete(staging);
                ReleaseLock(acquired.Lock.LockId, operationId, agentId, clientInstanceId);
            }
        }

        public bool VerifyDeployment(BridgeDeploymentManifest manifest)
        {
            if (manifest == null) return false;
            if (!string.Equals(manifest.ManifestFingerprint, ComputeManifestFingerprint(manifest),
                StringComparison.OrdinalIgnoreCase)) return false;
            string deploymentRoot = Path.Combine(root, SafePath(manifest.DeploymentSlot), "deployments",
                SafePath(manifest.DeploymentId));
            if (!Directory.Exists(deploymentRoot)) return false;
            HashSet<string> expected = new HashSet<string>(manifest.OutputArtifactHashes.Keys.Select(
                BridgeArtifactIdentity.NormalizeArtifactPath),
                StringComparer.OrdinalIgnoreCase);
            HashSet<string> actual = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
            string prefix = Path.GetFullPath(deploymentRoot).TrimEnd(Path.DirectorySeparatorChar) +
                Path.DirectorySeparatorChar;
            foreach (string file in Directory.GetFiles(deploymentRoot, "*", SearchOption.AllDirectories))
            {
                string relative = file.Substring(prefix.Length).Replace(Path.DirectorySeparatorChar, '/');
                if (string.Equals(relative, "deployment.manifest.json", StringComparison.OrdinalIgnoreCase) ||
                    string.Equals(relative, "deployment.manifest.json.lock", StringComparison.OrdinalIgnoreCase)) continue;
                actual.Add(relative);
            }
            if (!expected.SetEquals(actual)) return false;
            foreach (KeyValuePair<string, string> artifact in manifest.OutputArtifactHashes)
            {
                string path = Path.Combine(deploymentRoot, SafeRelativePath(artifact.Key));
                if (!File.Exists(path) || !string.Equals(BridgeHashing.FileSha256(path), artifact.Value,
                    StringComparison.OrdinalIgnoreCase)) return false;
            }
            return true;
        }

        public bool VerifyLoaded(BridgeDeploymentManifest manifest, string loadedAssemblyFingerprint)
        {
            return VerifyDeployment(manifest) && !string.IsNullOrWhiteSpace(loadedAssemblyFingerprint) &&
                string.Equals(manifest.LoadedAssemblyFingerprint, loadedAssemblyFingerprint,
                    StringComparison.OrdinalIgnoreCase);
        }

        public BridgeDeploymentManifest ReadCurrent(string deploymentSlot)
        {
            string slot = SafePath(deploymentSlot);
            string path = Path.Combine(root, slot, "current.json");
            BridgeDeploymentManifest manifest = BridgeDurableJson.Read<BridgeDeploymentManifest>(path);
            return manifest != null && VerifyDeployment(manifest) ? manifest : null;
        }

        private static string ComputeManifestFingerprint(BridgeDeploymentManifest manifest)
        {
            return BridgeHashing.Sha256(string.Join("\n", new[]
            {
                manifest.DeploymentId ?? string.Empty,
                manifest.ArtifactFingerprint ?? string.Empty,
                manifest.DeploymentSlot ?? string.Empty,
                manifest.SourceRevision ?? string.Empty,
                manifest.ProducingOperationId ?? string.Empty,
                manifest.ProducingAgentId ?? string.Empty,
                manifest.Provenance ?? string.Empty,
                manifest.LoadedAssemblyFingerprint ?? string.Empty,
                string.Join(";", manifest.OutputArtifactHashes.OrderBy(item => item.Key,
                    StringComparer.OrdinalIgnoreCase).Select(item => item.Key + "=" + item.Value)),
                string.Join(";", manifest.Assemblies.OrderBy(item => item.RelativePath,
                    StringComparer.OrdinalIgnoreCase).Select(item => string.Join("|", item.AssemblyName,
                    item.AssemblyVersion, item.ModuleVersionId, item.Sha256, item.RelativePath)))
            }));
        }

        private void LoadLocks()
        {
            string path = Path.Combine(root, "deployment-locks.json");
            List<BridgeDeploymentLockRecord> values = BridgeDurableJson.Read<List<BridgeDeploymentLockRecord>>(path);
            foreach (BridgeDeploymentLockRecord value in values ?? new List<BridgeDeploymentLockRecord>())
                if (!string.IsNullOrEmpty(value.DeploymentSlot)) locks[value.DeploymentSlot] = value;
            RemoveStaleLocksLocked();
        }

        private int RemoveStaleLocksLocked()
        {
            List<string> stale = locks.Where(item => item.Value.ExpiresAtUtc <= clock.UtcNow)
                .Where(item => !lockHandles.ContainsKey(item.Key))
                .Select(item => item.Key).ToList();
            foreach (string slot in stale)
            {
                locks.Remove(slot);
                ReleaseHandleLocked(slot);
            }
            return stale.Count;
        }

        private void ReleaseHandleLocked(string slot)
        {
            FileStream handle;
            if (!lockHandles.TryGetValue(slot, out handle)) return;
            lockHandles.Remove(slot);
            try { handle.Dispose(); } catch { }
            try { File.Delete(Path.Combine(root, ".locks", slot + ".lock")); } catch { }
        }

        private void PersistLocksLocked()
        {
            BridgeDurableJson.WriteAtomic(Path.Combine(root, "deployment-locks.json"), locks.Values.ToList());
        }

        private static string ExpectedHash(BridgeArtifactIdentity artifact, string relative)
        {
            string normalized = (relative ?? string.Empty).Replace('\\', '/').Trim('/');
            foreach (KeyValuePair<string, string> item in artifact.OutputArtifactHashes)
                if (string.Equals((item.Key ?? string.Empty).Replace('\\', '/').Trim('/'), normalized,
                    StringComparison.OrdinalIgnoreCase)) return item.Value;
            throw new InvalidDataException("deployment_output_not_in_artifact");
        }

        private static string SafePath(string value)
        {
            string result = (value ?? "default").Trim();
            if (!BridgeIdentityRules.IsValid(result, 96) || result == "." || result == "..")
                throw new ArgumentException("deployment_path_component_invalid");
            return result;
        }

        private static string SafeRelativePath(string value)
        {
            string normalized = (value ?? string.Empty).Replace('/', Path.DirectorySeparatorChar)
                .Replace('\\', Path.DirectorySeparatorChar);
            if (string.IsNullOrWhiteSpace(normalized) || Path.IsPathRooted(normalized) ||
                normalized.Split(new[] { Path.DirectorySeparatorChar }, StringSplitOptions.RemoveEmptyEntries)
                    .Any(item => item == ".." || item == "." || item.IndexOf(':') >= 0 ||
                        item.Any(character => !(char.IsLetterOrDigit(character) || character == '.' ||
                            character == '_' || character == '-'))))
                throw new ArgumentException("deployment_relative_path_invalid");
            return normalized;
        }

        private static void TryDelete(string path)
        {
            try { Directory.Delete(path, true); } catch { }
        }

        private static BridgeDeploymentLockRecord Clone(BridgeDeploymentLockRecord source)
        {
            return new BridgeDeploymentLockRecord
            {
                LockId = source.LockId,
                DeploymentSlot = source.DeploymentSlot,
                DeploymentId = source.DeploymentId,
                OperationId = source.OperationId,
                AgentId = source.AgentId,
                ClientInstanceId = source.ClientInstanceId,
                AcquiredAtUtc = source.AcquiredAtUtc,
                ExpiresAtUtc = source.ExpiresAtUtc
            };
        }
    }
}
