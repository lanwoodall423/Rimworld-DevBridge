using System;
using System.Threading;

namespace RimWorldDevBridge
{
    // Timer callbacks only enqueue work. Lease state is re-read and published on the owner thread.
    internal sealed class BridgeLeaseExpiryScheduler
    {
        private readonly object gate = new object();
        private readonly BridgeMainThreadContext mainThread;
        private readonly Func<bool> isShuttingDown;
        private readonly Func<BridgeRuntime.BridgeRuntimeStateSnapshot> captureSnapshot;
        private readonly Action markStateDirty;
        private readonly Action publishState;
        private readonly Action<long> mirrorExpiryTicks;
        private Timer timer;
        private long expiryTicks;
        private string session;

        internal BridgeLeaseExpiryScheduler(BridgeMainThreadContext mainThread, Func<bool> isShuttingDown,
            Func<BridgeRuntime.BridgeRuntimeStateSnapshot> captureSnapshot, Action markStateDirty,
            Action publishState, Action<long> mirrorExpiryTicks)
        {
            this.mainThread = mainThread;
            this.isShuttingDown = isShuttingDown;
            this.captureSnapshot = captureSnapshot;
            this.markStateDirty = markStateDirty;
            this.publishState = publishState;
            this.mirrorExpiryTicks = mirrorExpiryTicks;
        }

        internal long ScheduledExpiryTicks => Interlocked.Read(ref expiryTicks);

        internal void Schedule(BridgeRuntime.BridgeRuntimeStateSnapshot snapshot)
        {
            BridgeSessionContextSnapshot context = snapshot.Context;
            if (!context.WriteLeaseActive || !context.LeaseExpiresUtc.HasValue)
            {
                Cancel();
                return;
            }

            long expiry = context.LeaseExpiresUtc.Value.Ticks;
            mirrorExpiryTicks(expiry);
            lock (gate)
            {
                if (timer != null && expiryTicks == expiry &&
                    string.Equals(session, context.SessionId, StringComparison.Ordinal)) return;
                Timer previous = timer;
                timer = null;
                expiryTicks = expiry;
                session = context.SessionId;
                previous?.Dispose();
                long remainingTicks = expiry - DateTime.UtcNow.Ticks;
                int dueMs = remainingTicks <= 0 ? 1 :
                    (int)Math.Min(int.MaxValue, Math.Max(1d,
                        TimeSpan.FromTicks(remainingTicks).TotalMilliseconds));
                string expectedSession = context.SessionId;
                long version = snapshot.Version;
                int generation = snapshot.TransportGeneration;
                timer = new Timer(_ => OnExpiry(expectedSession, expiry, version, generation), null, dueMs,
                    Timeout.Infinite);
            }
        }

        internal void Cancel()
        {
            Timer previous;
            lock (gate)
            {
                previous = timer;
                timer = null;
                expiryTicks = 0;
                session = null;
                mirrorExpiryTicks(0);
            }
            try { previous?.Dispose(); } catch { }
        }

        private void OnExpiry(string expectedSession, long expectedExpiry, long version, int generation)
        {
            if (isShuttingDown()) return;
            try
            {
                mainThread.Post(_ =>
                {
                    if (isShuttingDown()) return;
                    BridgeRuntime.BridgeRuntimeStateSnapshot snapshot = captureSnapshot();
                    if (snapshot.Version != version || snapshot.TransportGeneration != generation)
                    {
                        Schedule(snapshot);
                        return;
                    }
                    BridgeSessionContextSnapshot context = snapshot.Context;
                    if (context.WriteLeaseActive && context.LeaseExpiresUtc.HasValue &&
                        string.Equals(context.SessionId, expectedSession, StringComparison.Ordinal) &&
                        context.LeaseExpiresUtc.Value.Ticks == expectedExpiry &&
                        context.LeaseExpiresUtc.Value > DateTime.UtcNow)
                    {
                        Schedule(snapshot);
                        return;
                    }
                    markStateDirty();
                    publishState();
                }, null);
            }
            catch { }
        }
    }
}
