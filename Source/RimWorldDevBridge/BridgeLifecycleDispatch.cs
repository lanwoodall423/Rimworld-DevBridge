using System;
using System.Threading;

namespace RimWorldDevBridge
{
    // Owns deferred lifecycle delivery, but leaves sequence validation and state publication to BridgeRuntime.
    internal sealed class BridgeLifecycleDispatch
    {
        private readonly BridgeMainThreadContext mainThread;
        private readonly Func<bool> isShuttingDown;
        private readonly Action<long> completeFinalizeInit;
        private int finalizeInitRequestThreadId;
        private int finalizeInitExecutionThreadId;
        private int finalizeInitDeferredCount;

        internal BridgeLifecycleDispatch(BridgeMainThreadContext mainThread, Func<bool> isShuttingDown,
            Action<long> completeFinalizeInit)
        {
            this.mainThread = mainThread;
            this.isShuttingDown = isShuttingDown;
            this.completeFinalizeInit = completeFinalizeInit;
        }

        internal int FinalizeInitRequestThreadId => Volatile.Read(ref finalizeInitRequestThreadId);
        internal int FinalizeInitExecutionThreadId => Volatile.Read(ref finalizeInitExecutionThreadId);
        internal int FinalizeInitDeferredCount => Volatile.Read(ref finalizeInitDeferredCount);
        internal int PendingCount => mainThread.LifecyclePendingCount;
        internal long CoalescedCount => mainThread.LifecycleCoalescedCount;
        internal long DroppedStaleCount => mainThread.LifecycleDroppedStaleCount;

        internal void OnFinalizeInit(long sequence)
        {
            Volatile.Write(ref finalizeInitRequestThreadId, Thread.CurrentThread.ManagedThreadId);
            if (!mainThread.IsOwnerEstablished || !mainThread.IsOwnerThread)
            {
                Interlocked.Increment(ref finalizeInitDeferredCount);
                PostFinalizeInit(sequence);
                return;
            }
            completeFinalizeInit(sequence);
        }

        internal void RecordExecutionThread()
        {
            Volatile.Write(ref finalizeInitExecutionThreadId, Thread.CurrentThread.ManagedThreadId);
        }

        internal void Post(Action callback)
        {
            if (callback == null) throw new ArgumentNullException(nameof(callback));
            mainThread.Post(_ => callback(), null);
        }

        internal void PostFinalizeInit(long sequence) =>
            mainThread.PostLifecycleLatest(CompleteDeferredFinalizeInit, sequence);

        private void CompleteDeferredFinalizeInit(object state)
        {
            if (isShuttingDown()) return;
            completeFinalizeInit((long)state);
        }
    }
}
