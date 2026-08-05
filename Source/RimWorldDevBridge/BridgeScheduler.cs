using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Linq;
using System.Security.Cryptography;
using System.Text;
using System.Threading;

namespace RimWorldDevBridge
{
    internal sealed class BridgeScheduler
    {
        private const int MaximumPerAgentQueue = 32;
        private readonly object gate = new object();
        private readonly Dictionary<string, Queue<BridgeRequest>> agentQueues =
            new Dictionary<string, Queue<BridgeRequest>>(StringComparer.Ordinal);
        private readonly Queue<string> agentRoundRobin = new Queue<string>();
        private readonly Dictionary<string, BridgeRequest> running =
            new Dictionary<string, BridgeRequest>(StringComparer.Ordinal);
        private readonly Dictionary<string, AgentMetric> agentMetrics =
            new Dictionary<string, AgentMetric>(StringComparer.Ordinal);
        private static long nextBarrierId;
        private readonly Func<BridgeRequest, BridgeResult> executor;
        private readonly Action<BridgeRequest, BridgeResult> completed;
        private SynchronizationContext mainContext;
        private string sessionId;
        private int capacity;
        private int budgetMs;
        private bool drainPosted;
        private long executed;
        private long rejected;
        private int queuedCount;
        private long drainBarrierId;
        private bool draining;
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
                draining = false;
                drainBarrierId = 0;
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
                if (queuedCount > 0) PostDrainLocked();
            }
        }

        internal int PerAgentQueueCapacity => MaximumPerAgentQueue;

        internal long BeginDrain()
        {
            lock (gate)
            {
                if (!draining)
                {
                    draining = true;
                    drainBarrierId = Math.Max(1L, Interlocked.Increment(ref nextBarrierId));
                }
                return drainBarrierId;
            }
        }

        internal bool IsDraining
        {
            get { lock (gate) return draining; }
        }

        internal long DrainBarrierId
        {
            get { lock (gate) return drainBarrierId; }
        }

        internal bool IsDrainComplete(BridgeRequest statusRequest = null)
        {
            lock (gate)
            {
                if (!draining) return false;
                foreach (Queue<BridgeRequest> pending in agentQueues.Values)
                    if (pending.Any(request => request.QueueBarrierId < drainBarrierId)) return false;
                foreach (BridgeRequest request in running.Values)
                    if (request != statusRequest && request.QueueBarrierId < drainBarrierId) return false;
                return true;
            }
        }

        internal BridgeResult DrainStatus(BridgeRequest statusRequest = null)
        {
            lock (gate)
            {
                bool complete = draining && IsDrainComplete(statusRequest);
                int preBarrierQueued = agentQueues.Values.SelectMany(items => items)
                    .Count(request => !draining || request.QueueBarrierId < drainBarrierId);
                int preBarrierRunning = running.Values.Count(request => request != statusRequest &&
                    (!draining || request.QueueBarrierId < drainBarrierId));
                return BridgeResult.Ok("core.restartDrainStatus")
                    .Add("draining", draining)
                    .Add("barrierId", drainBarrierId)
                    .Add("drained", complete)
                    .Add("preBarrierQueued", preBarrierQueued)
                    .Add("preBarrierRunning", preBarrierRunning)
                    .Add("queueDepth", queuedCount)
                    .Add("retry", complete ? "restart_ready_to_stop" : "restart_status");
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
                bool coordinatorRequest = IsCoordinatorRequest(request);
                if (draining && !coordinatorRequest)
                    return BridgeResult.Fail(BridgeStatus.BUSY, "restart_pending")
                        .Add("barrierId", drainBarrierId).Add("retry", "restart_status");
                if (agentQueues.Values.Any(items => items.Any(item => item.RequestId == request.RequestId)) ||
                    running.ContainsKey(request.RequestId))
                    return BridgeResult.Fail(BridgeStatus.INVALID_ARGUMENT, "duplicate_request_id");
                if (queuedCount >= capacity)
                {
                    rejected++;
                    return BridgeResult.Fail(BridgeStatus.BUSY, "operation_queue_full")
                        .Add("queueDepth", queuedCount).Add("capacity", capacity);
                }
                string agentKey = AgentKey(request.AgentId);
                if (!agentQueues.TryGetValue(agentKey, out Queue<BridgeRequest> agentQueue))
                {
                    agentQueue = new Queue<BridgeRequest>();
                    agentQueues[agentKey] = agentQueue;
                    agentRoundRobin.Enqueue(agentKey);
                }
                if (agentQueue.Count >= Math.Min(MaximumPerAgentQueue, capacity))
                    return BridgeResult.Fail(BridgeStatus.BUSY, "agent_queue_full")
                        .Add("agentQueueDepth", agentQueue.Count)
                        .Add("agentQueueCapacity", Math.Min(MaximumPerAgentQueue, capacity));
                request.QueueBarrierId = draining ? drainBarrierId : 0;
                agentQueue.Enqueue(request);
                queuedCount++;
                PostDrainLocked();
                return null;
            }
        }

        internal bool Cancel(string requestId)
        {
            return Cancel(requestId, null);
        }

        internal bool Cancel(string requestId, string agentId)
        {
            lock (gate)
            {
                BridgeRequest request = null;
                if (request == null)
                {
                    foreach (Queue<BridgeRequest> pending in agentQueues.Values)
                    {
                        request = pending.FirstOrDefault(item => item.RequestId == requestId);
                        if (request != null) break;
                    }
                }
                if (request == null) running.TryGetValue(requestId ?? string.Empty, out request);
                if (request == null || (agentId != null &&
                    !string.Equals(request.AgentId, agentId, StringComparison.Ordinal))) return false;
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
                stale = agentQueues.Values.SelectMany(items => items).ToList();
                agentQueues.Clear();
                agentRoundRobin.Clear();
                queuedCount = 0;
                draining = false;
                drainBarrierId = 0;
                foreach (BridgeRequest request in running.Values) request.Cancelled = true;
            }
            foreach (BridgeRequest request in stale)
                CompleteRejected(request, BridgeStatus.CANCELLED, "session_ended");
        }

        internal BridgeResult Metrics()
        {
            lock (gate)
            {
                DateTime? oldest = queuedCount == 0 ? (DateTime?)null :
                    agentQueues.Values.SelectMany(items => items).Min(item => item.EnqueuedUtc);
                BridgeResult result = BridgeResult.Ok("core.schedulerMetrics")
                    .Add("queueDepth", queuedCount)
                    .Add("capacity", capacity)
                    .Add("oldestMs", oldest.HasValue ? (DateTime.UtcNow - oldest.Value).TotalMilliseconds : 0d)
                    .Add("executed", executed)
                    .Add("rejected", rejected)
                    .Add("meanQueueMs", executed > 0 ? totalQueueMs / executed : 0d)
                    .Add("meanExecutionMs", executed > 0 ? totalExecutionMs / executed : 0d)
                    .Add("slowestMs", slowestMs)
                    .Add("slowestCommand", slowestCommand)
                    .Add("budgetMs", budgetMs)
                    .Add("perAgentQueueCapacity", MaximumPerAgentQueue)
                    .Add("draining", draining)
                    .Add("barrierId", drainBarrierId);
                foreach (KeyValuePair<string, AgentMetric> pair in agentMetrics.OrderBy(item => item.Key,
                    StringComparer.Ordinal).Take(32))
                {
                    AgentMetric metric = pair.Value;
                    result.AddLine("agent=" + RedactAgent(pair.Key) + " calls:" + metric.Calls +
                        " meanMs:" + (metric.Calls == 0 ? 0d : metric.TotalMs / metric.Calls) +
                        " maxMs:" + metric.MaxMs + " lastStatus:" + metric.LastStatus);
                }
                return result;
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
                if (queuedCount > 0) PostDrainLocked(yielded);
            }
        }

        private BridgeRequest NextLocked()
        {
            while (agentRoundRobin.Count > 0)
            {
                string agentKey = agentRoundRobin.Dequeue();
                if (!agentQueues.TryGetValue(agentKey, out Queue<BridgeRequest> pending) || pending.Count == 0)
                {
                    agentQueues.Remove(agentKey);
                    continue;
                }
                BridgeRequest request = pending.Dequeue();
                queuedCount--;
                if (pending.Count > 0) agentRoundRobin.Enqueue(agentKey);
                else agentQueues.Remove(agentKey);
                return request;
            }
            return null;
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
                    if (!stale) AddQueuedLocked(request);
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
                string agentKey = AgentKey(request.AgentId);
                if (!agentMetrics.TryGetValue(agentKey, out AgentMetric metric))
                {
                    metric = new AgentMetric();
                    agentMetrics[agentKey] = metric;
                }
                metric.Calls++;
                metric.TotalMs += executionMs;
                metric.MaxMs = Math.Max(metric.MaxMs, executionMs);
                metric.LastStatus = result.Status.ToString();
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

        private void AddQueuedLocked(BridgeRequest request)
        {
            string agentKey = AgentKey(request.AgentId);
            if (!agentQueues.TryGetValue(agentKey, out Queue<BridgeRequest> pending))
            {
                pending = new Queue<BridgeRequest>();
                agentQueues[agentKey] = pending;
                agentRoundRobin.Enqueue(agentKey);
            }
            pending.Enqueue(request);
            queuedCount++;
        }

        private static string AgentKey(string agentId)
        {
            return string.IsNullOrWhiteSpace(agentId) ? string.Empty : agentId;
        }

        private static string RedactAgent(string agentId)
        {
            if (string.IsNullOrEmpty(agentId)) return "anonymous";
            using (SHA256 sha = SHA256.Create())
                return "agent-" + BitConverter.ToString(sha.ComputeHash(Encoding.UTF8.GetBytes(agentId)))
                    .Replace("-", string.Empty).Substring(0, 12).ToLowerInvariant();
        }

        private static bool IsCoordinatorRequest(BridgeRequest request)
        {
            return request != null && (request.Command == "RESTART_DRAIN" ||
                request.Command == "RESTART_DRAIN_STATUS" || request.Command == "RESTART_HEARTBEAT");
        }

        private sealed class AgentMetric
        {
            internal long Calls;
            internal double TotalMs;
            internal double MaxMs;
            internal string LastStatus = "none";
        }
    }
}
