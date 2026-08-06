using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Threading;
using Verse;

namespace RimWorldDevBridge
{
    internal static class BridgeSnapshotProjection
    {
        private static int lastMaxItems;
        private static double lastMaxStepMs;

        internal static int LastMaxItemsForTests => Volatile.Read(ref lastMaxItems);
        internal static double LastMaxStepMsForTests => Volatile.Read(ref lastMaxStepMs);

        internal static void ResetMetricsForTests()
        {
            Interlocked.Exchange(ref lastMaxItems, 0);
            Volatile.Write(ref lastMaxStepMs, 0d);
        }

        internal sealed class Operation<T>
        {
            private readonly BridgeRequest request;
            private readonly BridgeQuery query;
            private readonly int mapId;
            private readonly int sourceCount;
            private readonly Func<int, T> sourceAt;
            private readonly Func<int> currentCount;
            private readonly Func<T, int> stableId;
            private readonly Func<T, bool> matches;
            private readonly Func<T, BridgeQuerySnapshotRow> project;
            private readonly string schema;
            private readonly List<BridgeQuerySnapshotRow> rows = new List<BridgeQuerySnapshotRow>();
            private readonly List<int> sourceIds = new List<int>();
            private readonly HashSet<int> stableIds = new HashSet<int>();
            private int index;
            private int validationIndex;
            private long estimatedBytes;

            internal Operation(BridgeRequest request, BridgeQuery query, int mapId, int sourceCount,
                Func<int, T> sourceAt, Func<int> currentCount, Func<T, int> stableId,
                Func<T, bool> matches, Func<T, BridgeQuerySnapshotRow> project, string schema)
            {
                this.request = request;
                this.query = query;
                this.mapId = mapId;
                this.sourceCount = sourceCount;
                this.sourceAt = sourceAt ?? throw new ArgumentNullException(nameof(sourceAt));
                this.currentCount = currentCount ?? throw new ArgumentNullException(nameof(currentCount));
                this.stableId = stableId ?? throw new ArgumentNullException(nameof(stableId));
                this.matches = matches ?? throw new ArgumentNullException(nameof(matches));
                this.project = project ?? throw new ArgumentNullException(nameof(project));
                this.schema = schema;
            }

            internal BridgeResult Step(BridgeExecutionContext context)
            {
                long stepStart = Stopwatch.GetTimestamp();
                int stepBudget = Math.Max(1, Math.Min(2, BridgeRuntime.EffectiveMainThreadBudgetMs));
                int processed = 0;
                try
                {
                    if (!StillValid(context) || currentCount() != sourceCount)
                        return Abort(context, "snapshot_source_changed", BridgeStatus.PARTIAL);
                    while (index < sourceCount)
                    {
                        context.ThrowIfCancellationRequested();
                        if (!StillValid(context) || currentCount() != sourceCount)
                            return Abort(context, "snapshot_source_changed", BridgeStatus.PARTIAL);
                        T value;
                        try { value = sourceAt(index++); }
                        catch (ArgumentOutOfRangeException)
                        {
                            return Abort(context, "snapshot_source_changed", BridgeStatus.PARTIAL);
                        }
                        catch (InvalidOperationException)
                        {
                            return Abort(context, "snapshot_source_changed", BridgeStatus.PARTIAL);
                        }
                        sourceIds.Add(value == null ? int.MinValue : stableId(value));
                        if (value != null && matches(value))
                        {
                            BridgeQuerySnapshotRow row = project(value);
                            if (row != null && !stableIds.Add(row.StableId))
                                return Abort(context, "snapshot_source_changed", BridgeStatus.PARTIAL);
                            if (row != null) BridgeDiagnostics.AddSnapshotRow(rows, row, ref estimatedBytes);
                        }
                        processed++;
                        if (processed >= 32 || BridgeTiming.Milliseconds(stepStart) >= stepBudget) break;
                    }
                    context.ThrowIfCancellationRequested();
                    if (index < sourceCount)
                    {
                        request.YieldExecution = true;
                        return null;
                    }

                    while (validationIndex < sourceCount)
                    {
                        context.ThrowIfCancellationRequested();
                        if (!StillValid(context) || currentCount() != sourceCount)
                            return Abort(context, "snapshot_source_changed", BridgeStatus.PARTIAL);
                        T value;
                        try { value = sourceAt(validationIndex); }
                        catch (ArgumentOutOfRangeException)
                        {
                            return Abort(context, "snapshot_source_changed", BridgeStatus.PARTIAL);
                        }
                        catch (InvalidOperationException)
                        {
                            return Abort(context, "snapshot_source_changed", BridgeStatus.PARTIAL);
                        }
                        int currentId = value == null ? int.MinValue : stableId(value);
                        if (currentId != sourceIds[validationIndex])
                            return Abort(context, "snapshot_source_changed", BridgeStatus.PARTIAL);
                        validationIndex++;
                        processed++;
                        if (processed >= 32 || BridgeTiming.Milliseconds(stepStart) >= stepBudget) break;
                    }
                    if (validationIndex < sourceCount)
                    {
                        request.YieldExecution = true;
                        return null;
                    }

                    BridgeDiagnostics.CheckSnapshotBudget(stepStart);
                    BridgeQuerySnapshot snapshot;
                    BridgeResult failure;
                    if (!BridgeQuerySnapshotStore.TryCreate(context.SessionId, request.Command,
                        query.CursorScope, query.Ordering, mapId, sourceCount, false, rows,
                        out snapshot, out failure))
                    {
                        request.CooperativeState = null;
                        return failure;
                    }
                    if (BridgeTiming.Milliseconds(stepStart) > BridgeRuntime.EffectiveMainThreadBudgetMs)
                    {
                        BridgeQuerySnapshotStore.Remove(snapshot.Id);
                        request.CooperativeState = null;
                        return BridgeDiagnostics.SnapshotTimeLimit();
                    }
                    query.SnapshotId = snapshot.Id;
                    query.SnapshotExpiryTicks = snapshot.ExpiresUtc.Ticks;
                    request.CooperativeState = null;
                    return BridgeDiagnostics.SnapshotPage(schema, request, query, snapshot);
                }
                catch (SnapshotMemoryExceededException)
                {
                    request.CooperativeState = null;
                    return BridgeDiagnostics.SnapshotMemoryLimit();
                }
                catch (SnapshotBudgetExceededException)
                {
                    request.CooperativeState = null;
                    return BridgeDiagnostics.SnapshotTimeLimit();
                }
                catch (OperationCanceledException)
                {
                    request.CooperativeState = null;
                    throw;
                }
                catch (Exception exception)
                {
                    request.CooperativeState = null;
                    return BridgeResult.Fail(BridgeStatus.ERROR, "snapshot_projection_failed",
                        exception.GetBaseException().Message);
                }
                finally
                {
                    int previousItems;
                    do
                    {
                        previousItems = Volatile.Read(ref lastMaxItems);
                        if (processed <= previousItems) break;
                    }
                    while (Interlocked.CompareExchange(ref lastMaxItems, processed,
                        previousItems) != previousItems);

                    double elapsed = BridgeTiming.Milliseconds(stepStart);
                    double previousStep;
                    do
                    {
                        previousStep = Volatile.Read(ref lastMaxStepMs);
                        if (elapsed <= previousStep) break;
                    }
                    while (Interlocked.CompareExchange(ref lastMaxStepMs, elapsed,
                        previousStep) != previousStep);
                }
            }

            private bool StillValid(BridgeExecutionContext context)
            {
                if (!string.Equals(request.SessionId, BridgeRuntime.SessionId, StringComparison.Ordinal)) return false;
                if (context.Map == null || context.Map.uniqueID != mapId) return false;
                Map currentMap = BridgeGameState.CurrentMap;
                return currentMap != null && currentMap.uniqueID == mapId;
            }

            private BridgeResult Abort(BridgeExecutionContext context, string code, BridgeStatus status)
            {
                request.CooperativeState = null;
                BridgeResult result = BridgeResult.Fail(status, code);
                result.Truncated = true;
                result.Warn("snapshot construction was discarded before a cursor was issued");
                return result;
            }
        }
    }

    internal sealed class SnapshotBudgetExceededException : Exception { }
    internal sealed class SnapshotMemoryExceededException : Exception { }
}
