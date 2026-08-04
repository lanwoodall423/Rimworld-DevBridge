using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Linq;
using System.Threading;

namespace RimWorldDevBridge
{
    internal sealed class BridgeScheduler
    {
        private readonly object gate = new object();
        private readonly List<BridgeRequest> queue = new List<BridgeRequest>();
        private readonly Dictionary<string, BridgeRequest> running =
            new Dictionary<string, BridgeRequest>(StringComparer.Ordinal);
        private readonly Func<BridgeRequest, BridgeResult> executor;
        private readonly Action<BridgeRequest, BridgeResult> completed;
        private SynchronizationContext mainContext;
        private string sessionId;
        private int capacity;
        private int budgetMs;
        private bool drainPosted;
        private long executed;
        private long rejected;
        private double totalQueueMs;
        private double totalExecutionMs;
        private double slowestMs;
        private string slowestCommand = "none";

        internal BridgeScheduler(Func<BridgeRequest, BridgeResult> executor,
            Action<BridgeRequest, BridgeResult> completed = null)
        {
            this.executor = executor ?? throw new ArgumentNullException(nameof(executor));
            this.completed = completed;
        }

        internal void Configure(SynchronizationContext context, string activeSessionId, int queueCapacity,
            int mainThreadBudgetMs)
        {
            lock (gate)
            {
                mainContext = context;
                sessionId = activeSessionId;
                capacity = Math.Max(8, Math.Min(256, queueCapacity));
                budgetMs = Math.Max(1, Math.Min(12, mainThreadBudgetMs));
            }
        }

        internal int QueueCapacity
        {
            get { lock (gate) return capacity; }
        }

        internal int MainThreadBudgetMs
        {
            get { lock (gate) return budgetMs; }
        }

        internal void Reconfigure(int queueCapacity, int mainThreadBudgetMs)
        {
            lock (gate)
            {
                // Reconfiguration changes admission and drain limits only. Existing queued and running
                // requests retain their session, cancellation, and execution state.
                capacity = Math.Max(8, Math.Min(256, queueCapacity));
                budgetMs = Math.Max(1, Math.Min(12, mainThreadBudgetMs));
                if (queue.Count > 0) PostDrainLocked();
            }
        }

        internal BridgeResult Enqueue(BridgeRequest request)
        {
            lock (gate)
            {
                if (mainContext == null)
                    return BridgeResult.Fail(BridgeStatus.UNAVAILABLE, "main_thread_unavailable");
                if (request.SessionId != sessionId)
                    return BridgeResult.Fail(BridgeStatus.INCOMPATIBLE, "stale_session");
                if (request.Expired)
                    return BridgeResult.Fail(BridgeStatus.TIMEOUT, "queue_deadline_expired");
                if (queue.Any(item => item.RequestId == request.RequestId) ||
                    running.ContainsKey(request.RequestId))
                    return BridgeResult.Fail(BridgeStatus.INVALID_ARGUMENT, "duplicate_request_id");
                if (queue.Count >= capacity)
                {
                    rejected++;
                    return BridgeResult.Fail(BridgeStatus.BUSY, "operation_queue_full")
                        .Add("queueDepth", queue.Count).Add("capacity", capacity);
                }
                queue.Add(request);
                PostDrainLocked();
                return null;
            }
        }

        internal bool Cancel(string requestId)
        {
            lock (gate)
            {
                BridgeRequest request = queue.FirstOrDefault(item => item.RequestId == requestId);
                if (request == null) running.TryGetValue(requestId ?? string.Empty, out request);
                if (request == null) return false;
                request.Cancelled = true;
                return true;
            }
        }

        internal void RotateSession(string newSessionId)
        {
            List<BridgeRequest> stale;
            lock (gate)
            {
                sessionId = newSessionId;
                stale = queue.ToList();
                queue.Clear();
                foreach (BridgeRequest request in running.Values) request.Cancelled = true;
            }
            foreach (BridgeRequest request in stale)
                CompleteRejected(request, BridgeStatus.CANCELLED, "session_ended");
        }

        internal BridgeResult Metrics()
        {
            lock (gate)
            {
                DateTime? oldest = queue.Count == 0 ? (DateTime?)null : queue.Min(item => item.EnqueuedUtc);
                return BridgeResult.Ok("core.schedulerMetrics")
                    .Add("queueDepth", queue.Count)
                    .Add("capacity", capacity)
                    .Add("oldestMs", oldest.HasValue ? (DateTime.UtcNow - oldest.Value).TotalMilliseconds : 0d)
                    .Add("executed", executed)
                    .Add("rejected", rejected)
                    .Add("meanQueueMs", executed > 0 ? totalQueueMs / executed : 0d)
                    .Add("meanExecutionMs", executed > 0 ? totalExecutionMs / executed : 0d)
                    .Add("slowestMs", slowestMs)
                    .Add("slowestCommand", slowestCommand)
                    .Add("budgetMs", budgetMs);
            }
        }

        private void PostDrainLocked()
        {
            PostDrainLocked(false);
        }

        private void PostDrainLocked(bool nextFrame)
        {
            if (drainPosted) return;
            drainPosted = true;
            BridgeMainThreadContext bridgeContext = mainContext as BridgeMainThreadContext;
            if (nextFrame && bridgeContext != null) bridgeContext.PostNextFrame(_ => Drain(), null);
            else mainContext.Post(_ => Drain(), null);
        }

        private void Drain()
        {
            long frameStart = Stopwatch.GetTimestamp();
            int operations = 0;
            bool yielded = false;
            while (operations < 4 && BridgeTiming.Milliseconds(frameStart) < MainThreadBudgetMs)
            {
                BridgeRequest request;
                lock (gate)
                {
                    request = NextLocked();
                    if (request == null)
                    {
                        drainPosted = false;
                        return;
                    }
                    queue.Remove(request);
                    running[request.RequestId] = request;
                }
                if (request.Cancelled)
                    CompleteRejected(request, BridgeStatus.CANCELLED, "cancelled_before_execution");
                else if (request.ClientDisconnected)
                    CompleteRejected(request, BridgeStatus.CANCELLED, "client_disconnected");
                else if (IsStaleSession(request))
                    CompleteRejected(request, BridgeStatus.INCOMPATIBLE, "stale_session");
                else if (request.Expired)
                    CompleteRejected(request, BridgeStatus.TIMEOUT, "queue_deadline_expired");
                else if (request.Cost >= BridgeCostClass.Expensive && !request.AllowExpensive)
                    CompleteRejected(request, BridgeStatus.FORBIDDEN, "expensive_override_required");
                else
                    yielded = Execute(request);
                operations++;
                if (yielded) break;
                if (request.Cost >= BridgeCostClass.Expensive) break;
            }
            lock (gate)
            {
                drainPosted = false;
                if (queue.Count > 0) PostDrainLocked(yielded);
            }
        }

        private BridgeRequest NextLocked()
        {
            return queue.OrderBy(item => item.Cancelled || item.ClientDisconnected ? -2 : (int)item.Cost)
                .ThenBy(item => item.EnqueuedUtc).FirstOrDefault();
        }

        private bool Execute(BridgeRequest request)
        {
            if (request.Cancelled || request.ClientDisconnected || request.Expired || IsStaleSession(request))
            {
                CompleteRejected(request, request.Expired ? BridgeStatus.TIMEOUT :
                    IsStaleSession(request) ? BridgeStatus.INCOMPATIBLE : BridgeStatus.CANCELLED,
                    request.Expired ? "queue_deadline_expired" :
                        IsStaleSession(request) ? "stale_session" : "cancelled_before_execution");
                return false;
            }
            request.Started = true;
            request.YieldExecution = false;
            long start = Stopwatch.GetTimestamp();
            BridgeResult result;
            try
            {
                result = executor(request);
            }
            catch (OperationCanceledException)
            {
                result = BridgeResult.Fail(request.Expired ? BridgeStatus.TIMEOUT : BridgeStatus.CANCELLED,
                    request.Expired ? "execution_deadline_expired" : "execution_cancelled");
            }
            catch (Exception exception)
            {
                result = BridgeResult.Fail(BridgeStatus.ERROR, exception.GetType().Name,
                    exception.GetBaseException().Message);
            }
            double stepMs = BridgeTiming.Milliseconds(start);
            bool yielded = request.YieldExecution && result == null;
            request.YieldExecution = false;
            if (request.Cancelled || request.ClientDisconnected || request.Expired || IsStaleSession(request))
            {
                CompleteRejected(request, request.Expired ? BridgeStatus.TIMEOUT :
                    IsStaleSession(request) ? BridgeStatus.INCOMPATIBLE : BridgeStatus.CANCELLED,
                    request.Expired ? "execution_deadline_expired" :
                        IsStaleSession(request) ? "stale_session" : "execution_cancelled");
                return false;
            }
            if (yielded)
            {
                request.CooperativeExecutionMs += stepMs;
                request.CooperativeSteps++;
                if (stepMs > request.CooperativeMaxStepMs) request.CooperativeMaxStepMs = stepMs;
                if (stepMs > MainThreadBudgetMs) request.CooperativeMainThreadOverrun = true;
                lock (gate)
                {
                    bool stale = request.Cancelled || request.ClientDisconnected || request.Expired ||
                        request.SessionId != sessionId;
                    running.Remove(request.RequestId);
                    if (!stale) queue.Add(request);
                }
                if (request.Cancelled || request.ClientDisconnected || request.Expired || request.SessionId != sessionId)
                {
                    CompleteRejected(request, request.Expired ? BridgeStatus.TIMEOUT :
                        request.SessionId != sessionId ? BridgeStatus.INCOMPATIBLE : BridgeStatus.CANCELLED,
                        request.Expired ? "execution_deadline_expired" :
                            request.SessionId != sessionId ? "stale_session" : "execution_cancelled");
                    return false;
                }
                return true;
            }
            if (result == null) result = BridgeResult.Fail(BridgeStatus.ERROR, "empty_result");
            double executionMs = request.CooperativeExecutionMs + stepMs;
            if (request.CooperativeExecutionMs > 0d || request.PreparedDescriptor?.Cooperative == true)
                request.CooperativeSteps++;
            result.ExecutionMs = executionMs;
            result.MainThreadBudgetMs = MainThreadBudgetMs;
            result.MainThreadOverrun = request.CooperativeMainThreadOverrun || stepMs > MainThreadBudgetMs;
            result.MaxMainThreadStepMs = Math.Max(stepMs, request.CooperativeMaxStepMs);
            result.CooperativeSteps = request.CooperativeSteps;
            result.QueueDelayMs = Math.Max(0d, (DateTime.UtcNow - request.EnqueuedUtc).TotalMilliseconds - executionMs);
            if (DateTime.UtcNow >= request.DeadlineUtc && result.Status == BridgeStatus.OK &&
                request.Mode == BridgeCommandMode.PureRead)
            {
                result.Status = BridgeStatus.TIMEOUT;
                result.Warn("Execution exceeded its deadline.");
            }
            else if (DateTime.UtcNow >= request.DeadlineUtc && request.Mode != BridgeCommandMode.PureRead)
                result.Warn("Mutation completed after its deadline; result retained to avoid ambiguous retries.");
            if (stepMs >= 50d || stepMs > MainThreadBudgetMs)
                result.Warn("slow main-thread step: " + stepMs.ToString("0.###") + " ms");
            if (request.CooperativeMainThreadOverrun && stepMs <= MainThreadBudgetMs)
                result.Warn("a prior cooperative main-thread step exceeded the configured budget");
            try { completed?.Invoke(request, result); }
            catch (Exception exception)
            {
                result.Warn("completion bookkeeping failed: " + exception.GetBaseException().Message);
            }
            request.Result = result;
            lock (gate)
            {
                running.Remove(request.RequestId);
                executed++;
                totalQueueMs += result.QueueDelayMs;
                totalExecutionMs += executionMs;
                if (executionMs > slowestMs)
                {
                    slowestMs = executionMs;
                    slowestCommand = request.Command;
                }
            }
            request.Done.Set();
            return false;
        }

        private void CompleteRejected(BridgeRequest request, BridgeStatus status, string code)
        {
            lock (gate) running.Remove(request.RequestId);
            request.Result = BridgeResult.Fail(status, code);
            request.Done.Set();
        }

        private bool IsStaleSession(BridgeRequest request)
        {
            lock (gate) return request.SessionId != sessionId;
        }
    }
}
