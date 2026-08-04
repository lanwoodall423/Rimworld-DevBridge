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
        private readonly Queue<WorkItem> nextFrameQueue = new Queue<WorkItem>();
        private int ownerThreadId = Thread.CurrentThread.ManagedThreadId;
        private bool ownerLocked;

        internal bool IsOwnerThread => Thread.CurrentThread.ManagedThreadId == Volatile.Read(ref ownerThreadId);

        // Mod construction can occur on a loader thread. The first authoritative Verse callback
        // establishes the actual game thread; ownership is immutable after that point.
        internal bool AdoptOwnerThread()
        {
            int current = Thread.CurrentThread.ManagedThreadId;
            lock (gate)
            {
                if (ownerLocked && ownerThreadId != current) return false;
                ownerThreadId = current;
                ownerLocked = true;
                return true;
            }
        }

        internal void AssertOwnerThread(string operation)
        {
            if (!IsOwnerThread)
                throw new InvalidOperationException(operation + " must run on the RimWorld main thread.");
        }

        public override void Post(SendOrPostCallback callback, object state)
        {
            if (callback == null) throw new ArgumentNullException(nameof(callback));
            lock (gate) queue.Enqueue(new WorkItem(callback, state));
        }

        internal void PostNextFrame(SendOrPostCallback callback, object state)
        {
            if (callback == null) throw new ArgumentNullException(nameof(callback));
            lock (gate) nextFrameQueue.Enqueue(new WorkItem(callback, state));
        }

        internal int Drain(int maxCallbacks, int budgetMs, Action<Exception> onError = null)
        {
            AssertOwnerThread("main-thread queue drain");
            lock (gate) ownerLocked = true;
            int limit = Math.Max(1, maxCallbacks);
            int budget = Math.Max(1, budgetMs);
            int drained = 0;
            long start = Stopwatch.GetTimestamp();
            lock (gate)
            {
                while (nextFrameQueue.Count > 0) queue.Enqueue(nextFrameQueue.Dequeue());
            }
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

    internal sealed class BridgeWakeSignal
    {
        private int signaled;

        internal void Signal()
        {
            Interlocked.Exchange(ref signaled, 1);
        }

        internal bool Consume()
        {
            return Interlocked.Exchange(ref signaled, 0) != 0;
        }
    }
}
