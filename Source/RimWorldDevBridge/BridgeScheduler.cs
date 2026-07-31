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
        private readonly Func<BridgeRequest, BridgeResult> executor;
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

        internal BridgeScheduler(Func<BridgeRequest, BridgeResult> executor)
        {
            this.executor = executor ?? throw new ArgumentNullException(nameof(executor));
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
            if (drainPosted) return;
            drainPosted = true;
            mainContext.Post(_ => Drain(), null);
        }

        private void Drain()
        {
            long frameStart = Stopwatch.GetTimestamp();
            int operations = 0;
            while (operations < 4 && BridgeTiming.Milliseconds(frameStart) < budgetMs)
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
                }
                if (request.Cancelled)
                    CompleteRejected(request, BridgeStatus.CANCELLED, "cancelled_before_execution");
                else if (request.ClientDisconnected)
                    CompleteRejected(request, BridgeStatus.CANCELLED, "client_disconnected");
                else if (request.SessionId != sessionId)
                    CompleteRejected(request, BridgeStatus.INCOMPATIBLE, "stale_session");
                else if (request.Expired)
                    CompleteRejected(request, BridgeStatus.TIMEOUT, "queue_deadline_expired");
                else if (request.Cost >= BridgeCostClass.Expensive && !request.AllowExpensive)
                    CompleteRejected(request, BridgeStatus.FORBIDDEN, "expensive_override_required");
                else
                    Execute(request);
                operations++;
            }
            lock (gate)
            {
                drainPosted = false;
                if (queue.Count > 0) PostDrainLocked();
            }
        }

        private BridgeRequest NextLocked()
        {
            return queue.OrderBy(item => item.Cancelled || item.ClientDisconnected ? -2 : (int)item.Cost)
                .ThenBy(item => item.EnqueuedUtc).FirstOrDefault();
        }

        private void Execute(BridgeRequest request)
        {
            request.Started = true;
            long start = Stopwatch.GetTimestamp();
            BridgeResult result;
            try
            {
                result = executor(request) ?? BridgeResult.Fail(BridgeStatus.ERROR, "empty_result");
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
            double executionMs = BridgeTiming.Milliseconds(start);
            result.ExecutionMs = executionMs;
            result.QueueDelayMs = Math.Max(0d, (DateTime.UtcNow - request.EnqueuedUtc).TotalMilliseconds - executionMs);
            if (DateTime.UtcNow >= request.DeadlineUtc && result.Status == BridgeStatus.OK &&
                request.Mode == BridgeCommandMode.PureRead)
            {
                result.Status = BridgeStatus.TIMEOUT;
                result.Warn("Execution exceeded its deadline.");
            }
            else if (DateTime.UtcNow >= request.DeadlineUtc && request.Mode != BridgeCommandMode.PureRead)
                result.Warn("Mutation completed after its deadline; result retained to avoid ambiguous retries.");
            request.Result = result;
            lock (gate)
            {
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
        }

        private static void CompleteRejected(BridgeRequest request, BridgeStatus status, string code)
        {
            request.Result = BridgeResult.Fail(status, code);
            request.Done.Set();
        }
    }
}
