using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Globalization;
using System.Threading;

namespace RimWorldDevBridge
{
    internal sealed class BridgeStatusPublication
    {
        internal readonly BridgeRuntime.BridgeRuntimeStateSnapshot Snapshot;
        internal readonly string State;
        internal readonly string Extra;
        internal readonly double BootstrapMs;
        internal readonly double HarmonyMs;
        internal readonly double FinalizeInitMs;
        internal readonly double ActivationMs;
        internal readonly long BootstrapManagedDeltaBytes;

        internal BridgeStatusPublication(BridgeRuntime.BridgeRuntimeStateSnapshot snapshot, string state, string extra,
            double bootstrapMs, double harmonyMs, double finalizeInitMs, double activationMs,
            long bootstrapManagedDeltaBytes)
        {
            Snapshot = snapshot;
            State = state;
            Extra = extra;
            BootstrapMs = bootstrapMs;
            HarmonyMs = harmonyMs;
            FinalizeInitMs = finalizeInitMs;
            ActivationMs = activationMs;
            BootstrapManagedDeltaBytes = bootstrapManagedDeltaBytes;
        }
    }

    internal static class BridgeStatusPublisher
    {
        private static readonly object Gate = new object();
        private static double lastWriteMs;
        private static int writeCount;

        internal static int WriteCountForTests => Volatile.Read(ref writeCount);

        internal static void ResetWriteCountForTests() => Interlocked.Exchange(ref writeCount, 0);

        internal static void DeleteIf(Func<bool> shouldDelete)
        {
            lock (Gate)
            {
                if (shouldDelete()) BridgeFileOperations.TryDelete(BridgePaths.StatusPath);
            }
        }

        internal static bool Write(BridgeStatusPublication publication, Func<long> currentVersion)
        {
            lock (Gate)
            {
                if (publication.Snapshot.Version != currentVersion()) return false;
                long writeStart = Stopwatch.GetTimestamp();
                BridgeRuntime.BridgeRuntimeStateSnapshot snapshot = publication.Snapshot;
                BridgeSessionContextSnapshot context = snapshot.Context;
                List<string> lines = new List<string>
                {
                    "bridge=" + publication.State,
                    "name=RimWorld Dev Bridge",
                    "version=" + BridgeProtocol.BridgeVersion,
                    "protocol=" + BridgeProtocol.ProtocolVersion,
                    "schema=" + BridgeProtocol.CoreSchema,
                    "coreFingerprint=" + BridgeRuntime.CoreFingerprint,
                    "processId=" + BridgeRuntime.ProcessIdForClients,
                    "processStartIdentity=" + Process.GetCurrentProcess().StartTime.ToUniversalTime().Ticks,
                    "bootId=" + BridgeRuntime.BootIdForClients,
                    "statusUtc=" + DateTime.UtcNow.ToString("o", CultureInfo.InvariantCulture),
                    "session=" + snapshot.Context.SessionId,
                    "transport=" + (snapshot.TransportActive ? "tcp+file" : "wake-file"),
                    "host=127.0.0.1",
                    "port=" + snapshot.Port,
                    "token=" + snapshot.TransportToken,
                    "clients=" + snapshot.ConnectedClients + "/" + snapshot.ConnectedClientLimit,
                    "context=" + context.WriteContext,
                    "writeContext=" + context.WriteContext,
                    "representativePlayerBehavior=" + context.RepresentativePlayerBehavior,
                    "writeLeaseActive=" + context.WriteLeaseActive,
                    "leaseState=" + context.LeaseState,
                    "leaseExpiresUtc=" + (context.LeaseExpiresUtc?.ToString("o", CultureInfo.InvariantCulture) ?? "none"),
                    "remoteMutationEnabled=" + snapshot.RemoteMutationEnabled,
                    "mutationConfirmation=" + snapshot.MutationConfirmation.State,
                    "mutationGameLoaded=" + snapshot.MutationConfirmation.GameLoaded,
                     "mutationConfirmed=" + snapshot.MutationConfirmation.Confirmed,
                     "transportGeneration=" + snapshot.TransportGeneration,
                     "lifecycleGeneration=" + snapshot.LifecycleGeneration,
                     "adapterFingerprint=" + BridgeAdapterCatalog.Fingerprint,
                       "runtimeSlotId=" + (BridgeRuntime.ActiveRuntimeSlotId ?? "none"),
                       "artifactFingerprint=" + BridgeRuntime.ArtifactFingerprint,
                      "loadedAssemblyFingerprint=" + BridgeRuntime.LoadedAssemblyFingerprint,
                    "bootstrapMs=" + publication.BootstrapMs.ToString("0.###", CultureInfo.InvariantCulture),
                    "harmonyMs=" + publication.HarmonyMs.ToString("0.###", CultureInfo.InvariantCulture),
                    "finalizeInitMs=" + publication.FinalizeInitMs.ToString("0.###", CultureInfo.InvariantCulture),
                    "activationMs=" + publication.ActivationMs.ToString("0.###", CultureInfo.InvariantCulture),
                    "statusWriteMs=" + lastWriteMs.ToString("0.###", CultureInfo.InvariantCulture),
                    "bootstrapManagedDeltaBytesApprox=" + publication.BootstrapManagedDeltaBytes,
                    "adapterIndex=" + BridgeAdapterCatalog.State,
                    "input=" + BridgePaths.InputPath,
                    "output=" + BridgePaths.OutputPath
                };
                if (!string.IsNullOrEmpty(publication.Extra)) lines.Add(publication.Extra);
                if (!BridgeFileOperations.AtomicWrite(BridgePaths.StatusPath, string.Join("\n", lines))) return false;
                lastWriteMs = BridgeTiming.Milliseconds(writeStart);
                Interlocked.Increment(ref writeCount);
                return true;
            }
        }
    }
}
