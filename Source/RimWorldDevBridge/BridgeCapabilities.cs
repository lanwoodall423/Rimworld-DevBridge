using System;
using System.Collections.Generic;
using System.Linq;

namespace RimWorldDevBridge
{
    internal static class BridgeCapabilities
    {
        internal const string Version = "stage2.serialized-v1";

        internal static BridgeResult Create(IEnumerable<BridgeCommandDescriptor> descriptors)
        {
            List<BridgeCommandDescriptor> commands = (descriptors ?? Enumerable.Empty<BridgeCommandDescriptor>())
                .Where(item => item != null).OrderBy(item => item.Name, StringComparer.Ordinal).ToList();
            BridgeResult result = BridgeResult.Ok("core.capabilities");
            result.CapabilityVersion = Version;
            result.SupportedOperationStates.AddRange(Enum.GetNames(typeof(BridgeOperationState)));
            result.SupportedOperationKinds.AddRange(Enum.GetNames(typeof(BridgeOperationKind)));
            result.ReadOperations.AddRange(commands.Where(IsSafeRead)
                .Select(item => item.Name).Distinct(StringComparer.OrdinalIgnoreCase));
            List<string> mutationClasses = commands.Where(item => !IsSafeRead(item))
                .Select(MutationClass).ToList();
            mutationClasses.AddRange(new[] { "LeaseWrite", "AdapterReload", "RestartDrain" });
            result.MutationClasses.AddRange(mutationClasses.Distinct(StringComparer.OrdinalIgnoreCase));
            result.SupportedRuntimeSlotCount = BridgeRuntime.SupportedRuntimeSlotCount;
            result.ConcurrentReadDiagnostics = true;
            result.BuildProvider = "loaded-core-assembly";
            result.DeploymentProvider = "atomic-fingerprint-bound-manifest";
            result.AdapterReloadSupported = commands.Any(item =>
                string.Equals(item.Name, "RELOAD_ADAPTERS", StringComparison.OrdinalIgnoreCase) ||
                string.Equals(item.Name, "RELOAD_HOT_ADAPTERS", StringComparison.OrdinalIgnoreCase));
            result.SaveFixtureSupported = false;
            result.EvidenceTypes.AddRange(new[] { "status", "operation", "process", "deployment", "artifact", "redacted-diagnostics" });
            result.AuthorizationMechanism = "client-credential+write-lease+managed-sandbox";
            result.PlatformRestrictions.AddRange(new[]
            {
                "one-coordinator-per-user",
                "one-managed-runtime-slot",
                "one-mutating-workflow",
                "owner-thread-Unity-Verse-access"
            });
            result.Add("capabilityVersion", Version)
                .Add("supportedOperationStates", string.Join(",", result.SupportedOperationStates))
                .Add("supportedOperationKinds", string.Join(",", result.SupportedOperationKinds))
                .Add("supportedRuntimeSlotCount", result.SupportedRuntimeSlotCount)
                .Add("concurrentReadDiagnostics", result.ConcurrentReadDiagnostics)
                .Add("authorizationMechanism", result.AuthorizationMechanism);
            foreach (string operation in result.ReadOperations) result.AddLine("read=" + operation);
            foreach (string mutation in result.MutationClasses) result.AddLine("mutation=" + mutation);
            return result;
        }

        private static bool IsSafeRead(BridgeCommandDescriptor descriptor)
        {
            if (descriptor == null || descriptor.Mode != BridgeCommandMode.PureRead) return false;
            string name = descriptor.Name ?? string.Empty;
            return !name.Equals("WRITE_LEASE", StringComparison.OrdinalIgnoreCase) &&
                !name.Equals("RENEW_WRITE_LEASE", StringComparison.OrdinalIgnoreCase) &&
                !name.Equals("REVOKE_WRITE_LEASE", StringComparison.OrdinalIgnoreCase) &&
                !name.Equals("RESTART_DRAIN", StringComparison.OrdinalIgnoreCase) &&
                !name.Equals("RESTART_DRAIN_STATUS", StringComparison.OrdinalIgnoreCase) &&
                !name.Equals("RESTART_HEARTBEAT", StringComparison.OrdinalIgnoreCase) &&
                !name.Equals("RELOAD_ADAPTERS", StringComparison.OrdinalIgnoreCase) &&
                !name.Equals("RELOAD_HOT_ADAPTERS", StringComparison.OrdinalIgnoreCase) &&
                !name.Equals("RELOAD_BRIDGE", StringComparison.OrdinalIgnoreCase);
        }

        private static string MutationClass(BridgeCommandDescriptor descriptor)
        {
            string name = descriptor?.Name ?? string.Empty;
            if (name.IndexOf("LEASE", StringComparison.OrdinalIgnoreCase) >= 0) return "LeaseWrite";
            if (name.IndexOf("RELOAD", StringComparison.OrdinalIgnoreCase) >= 0) return "AdapterReload";
            if (name.IndexOf("RESTART", StringComparison.OrdinalIgnoreCase) >= 0) return "RestartDrain";
            return descriptor?.Mode.ToString() ?? "Unknown";
        }
    }
}
