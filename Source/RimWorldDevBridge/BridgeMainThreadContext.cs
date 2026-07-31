using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Threading;

namespace RimWorldDevBridge
{
    internal sealed class BridgeMainThreadContext : SynchronizationContext
    {
        private readonly object gate = new object();
        private readonly Queue<WorkItem> queue = new Queue<WorkItem>();

        public override void Post(SendOrPostCallback callback, object state)
        {
            if (callback == null) throw new ArgumentNullException(nameof(callback));
            lock (gate) queue.Enqueue(new WorkItem(callback, state));
        }

        internal int Drain(int maxCallbacks, int budgetMs, Action<Exception> onError = null)
        {
            int limit = Math.Max(1, maxCallbacks);
            int budget = Math.Max(1, budgetMs);
            int drained = 0;
            long start = Stopwatch.GetTimestamp();
            while (drained < limit && BridgeTiming.Milliseconds(start) < budget)
            {
                WorkItem item;
                lock (gate)
                {
                    if (queue.Count == 0) break;
                    item = queue.Dequeue();
                }
                try { item.Callback(item.State); }
                catch (Exception exception) { onError?.Invoke(exception); }
                drained++;
            }
            return drained;
        }

        private sealed class WorkItem
        {
            internal readonly SendOrPostCallback Callback;
            internal readonly object State;

            internal WorkItem(SendOrPostCallback callback, object state)
            {
                Callback = callback;
                State = state;
            }
        }
    }
}
