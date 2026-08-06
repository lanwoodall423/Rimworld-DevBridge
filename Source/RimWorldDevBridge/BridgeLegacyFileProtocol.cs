using System;
using System.Diagnostics;
using System.IO;
using System.Threading;

namespace RimWorldDevBridge
{
    // Handles the bounded file protocol after the owner thread has observed an input signal.
    internal sealed class BridgeLegacyFileProtocol
    {
        private readonly Action assertMainThread;
        private readonly Func<string> sessionId;
        private readonly BridgeRequestPreparation preparation;
        private readonly BridgeScheduler scheduler;
        private readonly Func<BridgeResult, BridgeRequest, string, string, BridgeResult> decorate;

        internal BridgeLegacyFileProtocol(Action assertMainThread, Func<string> sessionId,
            BridgeRequestPreparation preparation, BridgeScheduler scheduler,
            Func<BridgeResult, BridgeRequest, string, string, BridgeResult> decorate)
        {
            this.assertMainThread = assertMainThread;
            this.sessionId = sessionId;
            this.preparation = preparation;
            this.scheduler = scheduler;
            this.decorate = decorate;
        }

        internal void Process()
        {
            assertMainThread();
            string inputPath = BridgePaths.InputPath;
            string outputPath = BridgePaths.OutputPath;
            if (!File.Exists(inputPath)) return;
            try
            {
                string raw = File.ReadAllText(inputPath).Trim();
                File.Delete(inputPath);
                if (!BridgeProtocol.TryParse(raw, sessionId(), out BridgeRequest request,
                    out BridgeResult failure))
                {
                    BridgeFileOperations.AtomicWrite(outputPath, BridgeProtocol.Serialize(failure, "line"));
                    return;
                }
                long prepareStart = Stopwatch.GetTimestamp();
                BridgePreparationResult prepared = preparation.Prepare(request);
                BridgeResult prepare = prepared.Failure;
                BridgeCommandDescriptor descriptor = prepared.Descriptor;
                if (descriptor == null)
                {
                    request.PreparationMs = BridgeTiming.Milliseconds(prepareStart);
                    failure = prepare ?? BridgeResult.Fail(BridgeStatus.NOT_FOUND, "unknown_command");
                    decorate(failure, request, "core", BridgeProtocol.BridgeVersion);
                    BridgeFileOperations.AtomicWrite(outputPath, BridgeProtocol.Serialize(failure, "line"));
                    return;
                }
                request.PreparationMs = BridgeTiming.Milliseconds(prepareStart);
                if (prepare != null)
                {
                    decorate(prepare, request, descriptor.Provider, descriptor.ProviderVersion);
                    BridgeFileOperations.AtomicWrite(outputPath, BridgeProtocol.Serialize(prepare, "line"));
                    return;
                }
                request.EnqueuedUtc = DateTime.UtcNow;
                BridgeResult enqueue = scheduler.Enqueue(request);
                if (enqueue != null)
                {
                    decorate(enqueue, request, descriptor.Provider, descriptor.ProviderVersion);
                    BridgeFileOperations.AtomicWrite(outputPath, BridgeProtocol.Serialize(enqueue, "line"));
                    return;
                }
                ThreadPool.QueueUserWorkItem(_ =>
                {
                    request.Done.Wait(Math.Max(50, (int)request.Remaining.TotalMilliseconds));
                    BridgeResult result = request.Result ?? BridgeResult.Fail(BridgeStatus.TIMEOUT,
                        "file_request_timeout");
                    decorate(result, request, descriptor.Provider, descriptor.ProviderVersion);
                    BridgeFileOperations.AtomicWrite(outputPath, BridgeProtocol.Serialize(result, "line"));
                });
            }
            catch { }
        }
    }
}
