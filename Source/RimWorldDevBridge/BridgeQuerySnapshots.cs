using System;
using System.Collections.Generic;
using System.Collections.ObjectModel;
using System.Linq;
using System.Text;

namespace RimWorldDevBridge
{
    internal sealed class BridgeQuerySnapshotRow
    {
        internal int StableId { get; }
        internal string Line { get; }
        internal long EstimatedBytes => (long)Line.Length * 2L + 96L;

        internal BridgeQuerySnapshotRow(int stableId, string line)
        {
            StableId = stableId;
            Line = BoundLine(line);
        }

        private static string BoundLine(string value)
        {
            string line = BridgeText.Bound(value ?? string.Empty, BridgeProtocol.MaxLineBytes);
            if (Encoding.UTF8.GetByteCount(line) <= BridgeProtocol.MaxLineBytes) return line;

            int low = 0;
            int high = line.Length;
            while (low < high)
            {
                int middle = (low + high + 1) / 2;
                if (Encoding.UTF8.GetByteCount(line.Substring(0, middle)) <= BridgeProtocol.MaxLineBytes) low = middle;
                else high = middle - 1;
            }
            return line.Substring(0, low);
        }
    }

    internal sealed class BridgeQuerySnapshot
    {
        internal string Id { get; }
        internal string SessionId { get; }
        internal string Command { get; }
        internal string Scope { get; }
        internal string Ordering { get; }
        internal int MapId { get; }
        internal DateTime ExpiresUtc { get; }
        internal int Scanned { get; }
        internal int Available { get; }
        internal bool ScanTruncated { get; }
        internal IReadOnlyList<BridgeQuerySnapshotRow> Rows { get; }
        internal long EstimatedBytes { get; }

        internal BridgeQuerySnapshot(string sessionId, string command, string scope, string ordering,
            int mapId, int scanned, int available, bool scanTruncated, IList<BridgeQuerySnapshotRow> rows,
            DateTime expiresUtc, long estimatedBytes)
        {
            Id = Guid.NewGuid().ToString("N");
            SessionId = sessionId ?? string.Empty;
            Command = BridgeText.NormalizeCommand(command);
            Scope = scope ?? string.Empty;
            Ordering = ordering ?? string.Empty;
            MapId = mapId;
            Scanned = scanned;
            Available = available;
            ScanTruncated = scanTruncated;
            ExpiresUtc = expiresUtc;
            EstimatedBytes = estimatedBytes;
            Rows = new ReadOnlyCollection<BridgeQuerySnapshotRow>(rows ?? new List<BridgeQuerySnapshotRow>());
        }
    }

    internal static class BridgeQuerySnapshotStore
    {
        internal const int DefaultMaximumSnapshots = 32;
        internal const int DefaultMaximumRows = 20000;
        internal const int DefaultMaximumBytes = 8 * 1024 * 1024;
        internal static readonly TimeSpan DefaultLifetime = TimeSpan.FromSeconds(60);

        private static readonly object Gate = new object();
        private static readonly Dictionary<string, BridgeQuerySnapshot> Snapshots =
            new Dictionary<string, BridgeQuerySnapshot>(StringComparer.Ordinal);
        private static Limits limits = new Limits(DefaultMaximumSnapshots, DefaultMaximumRows,
            DefaultMaximumBytes, DefaultLifetime);

        internal static int MaximumRows
        {
            get { lock (Gate) return limits.MaximumRows; }
        }

        internal static int MaximumBytes
        {
            get { lock (Gate) return limits.MaximumBytes; }
        }

        internal static long AvailableBytes
        {
            get
            {
                lock (Gate)
                {
                    long used = Snapshots.Values.Sum(snapshot => snapshot.EstimatedBytes);
                    return Math.Max(0L, (long)limits.MaximumBytes - used);
                }
            }
        }

        internal static bool CanCreate(out BridgeResult failure)
        {
            lock (Gate)
            {
                RemoveExpiredLocked(DateTime.UtcNow);
                if (Snapshots.Count >= limits.MaximumSnapshots)
                {
                    failure = SnapshotLimit("snapshot_count_limit", "query snapshot count limit reached");
                    return false;
                }
                if (Snapshots.Values.Sum(snapshot => snapshot.EstimatedBytes) >= limits.MaximumBytes)
                {
                    failure = SnapshotLimit("snapshot_memory_limit", "query snapshot memory limit reached");
                    return false;
                }
                failure = null;
                return true;
            }
        }

        internal static bool TryCreate(string sessionId, string command, string scope, string ordering, int mapId,
            int scanned, bool scanTruncated, IEnumerable<BridgeQuerySnapshotRow> rows,
            out BridgeQuerySnapshot snapshot, out BridgeResult failure)
        {
            snapshot = null;
            failure = null;
            List<BridgeQuerySnapshotRow> boundedRows = new List<BridgeQuerySnapshotRow>();
            int maximumRows;
            lock (Gate) maximumRows = limits.MaximumRows;
            foreach (BridgeQuerySnapshotRow row in rows ?? Enumerable.Empty<BridgeQuerySnapshotRow>())
            {
                if (row == null) continue;
                if (boundedRows.Count >= maximumRows)
                {
                    failure = SnapshotLimit("snapshot_row_limit", "query snapshot row limit reached");
                    return false;
                }
                boundedRows.Add(row);
            }
            boundedRows.Sort((left, right) =>
            {
                int comparison = left.StableId.CompareTo(right.StableId);
                return comparison != 0 ? comparison : string.CompareOrdinal(left.Line, right.Line);
            });

            long estimatedBytes = boundedRows.Sum(row => row.EstimatedBytes);
            DateTime now = DateTime.UtcNow;
            lock (Gate)
            {
                RemoveExpiredLocked(now);
                if (Snapshots.Count >= limits.MaximumSnapshots)
                {
                    failure = SnapshotLimit("snapshot_count_limit", "query snapshot count limit reached");
                    return false;
                }
                long retainedBytes = Snapshots.Values.Sum(item => item.EstimatedBytes);
                if (estimatedBytes > limits.MaximumBytes ||
                    retainedBytes > limits.MaximumBytes - estimatedBytes)
                {
                    failure = SnapshotLimit("snapshot_memory_limit", "query snapshot memory limit reached");
                    return false;
                }

                if (boundedRows.Count > limits.MaximumRows)
                {
                    failure = SnapshotLimit("snapshot_row_limit", "query snapshot row limit reached");
                    return false;
                }
                snapshot = new BridgeQuerySnapshot(sessionId, command, scope, ordering, mapId, scanned, scanned,
                    scanTruncated, boundedRows, now.Add(limits.Lifetime), estimatedBytes);
                Snapshots.Add(snapshot.Id, snapshot);
                return true;
            }
        }

        internal static bool TryGet(string sessionId, string command, string scope, string ordering, int mapId,
            string snapshotId, long expiryTicks, out BridgeQuerySnapshot snapshot, out BridgeResult failure)
        {
            snapshot = null;
            failure = null;
            DateTime now = DateTime.UtcNow;
            lock (Gate)
            {
                if (!string.IsNullOrWhiteSpace(snapshotId) && Snapshots.TryGetValue(snapshotId, out snapshot) &&
                    snapshot.ExpiresUtc <= now)
                {
                    Snapshots.Remove(snapshot.Id);
                    snapshot = null;
                    failure = BridgeResult.Fail(BridgeStatus.INVALID_ARGUMENT, "cursor_expired");
                    return false;
                }
                RemoveExpiredLocked(now);
                if (string.IsNullOrWhiteSpace(snapshotId) || !Snapshots.TryGetValue(snapshotId, out snapshot))
                {
                    failure = BridgeResult.Fail(BridgeStatus.INVALID_ARGUMENT, "cursor_snapshot_unavailable");
                    return false;
                }
                if (snapshot.ExpiresUtc <= now)
                {
                    Snapshots.Remove(snapshot.Id);
                    snapshot = null;
                    failure = BridgeResult.Fail(BridgeStatus.INVALID_ARGUMENT, "cursor_expired");
                    return false;
                }
                if (!string.Equals(snapshot.SessionId, sessionId ?? string.Empty, StringComparison.Ordinal))
                    return Mismatch(out snapshot, out failure, "cursor_session_mismatch");
                if (!string.Equals(snapshot.Command, BridgeText.NormalizeCommand(command), StringComparison.Ordinal))
                    return Mismatch(out snapshot, out failure, "cursor_query_mismatch");
                if (!string.Equals(snapshot.Scope, scope ?? string.Empty, StringComparison.Ordinal))
                    return Mismatch(out snapshot, out failure, "cursor_filter_mismatch");
                if (!string.Equals(snapshot.Ordering, ordering ?? string.Empty, StringComparison.Ordinal))
                    return Mismatch(out snapshot, out failure, "cursor_order_mismatch");
                if (snapshot.MapId != mapId)
                    return Mismatch(out snapshot, out failure, "cursor_map_mismatch");
                if (expiryTicks != snapshot.ExpiresUtc.Ticks)
                    return Mismatch(out snapshot, out failure, "cursor_snapshot_mismatch");
                return true;
            }
        }

        internal static void Remove(string snapshotId)
        {
            if (string.IsNullOrWhiteSpace(snapshotId)) return;
            lock (Gate) Snapshots.Remove(snapshotId);
        }

        internal static void RotateSession()
        {
            lock (Gate) Snapshots.Clear();
        }

        internal static void CleanupStaleMaps(IEnumerable<int> activeMapIds)
        {
            HashSet<int> active = activeMapIds == null ? new HashSet<int>() :
                new HashSet<int>(activeMapIds);
            lock (Gate)
            {
                RemoveExpiredLocked(DateTime.UtcNow);
                foreach (string id in Snapshots.Where(pair => !active.Contains(pair.Value.MapId))
                    .Select(pair => pair.Key).ToList()) Snapshots.Remove(id);
            }
        }

        internal static int ActiveCount
        {
            get { lock (Gate) return Snapshots.Count; }
        }

        internal static long ActiveBytes
        {
            get { lock (Gate) return Snapshots.Values.Sum(snapshot => snapshot.EstimatedBytes); }
        }

        internal static void ConfigureLimitsForTests(int maximumSnapshots, int maximumRows, int maximumBytes,
            TimeSpan lifetime)
        {
            if (maximumSnapshots < 1 || maximumRows < 1 || maximumBytes < 1 || lifetime <= TimeSpan.Zero)
                throw new ArgumentOutOfRangeException();
            lock (Gate)
            {
                Snapshots.Clear();
                limits = new Limits(maximumSnapshots, maximumRows, maximumBytes, lifetime);
            }
        }

        internal static void ResetLimitsForTests()
        {
            lock (Gate)
            {
                Snapshots.Clear();
                limits = new Limits(DefaultMaximumSnapshots, DefaultMaximumRows, DefaultMaximumBytes,
                    DefaultLifetime);
            }
        }

        private static bool Mismatch(out BridgeQuerySnapshot snapshot, out BridgeResult failure, string code)
        {
            snapshot = null;
            failure = BridgeResult.Fail(BridgeStatus.INVALID_ARGUMENT, code);
            return false;
        }

        private static BridgeResult SnapshotLimit(string code, string detail) =>
            BridgeResult.Fail(BridgeStatus.BUSY, code, detail).Warn("query snapshot was not retained; retry after cleanup");

        private static void RemoveExpiredLocked(DateTime now)
        {
            foreach (string id in Snapshots.Where(pair => pair.Value.ExpiresUtc <= now)
                .Select(pair => pair.Key).ToList()) Snapshots.Remove(id);
        }

        private struct Limits
        {
            internal readonly int MaximumSnapshots;
            internal readonly int MaximumRows;
            internal readonly int MaximumBytes;
            internal readonly TimeSpan Lifetime;

            internal Limits(int maximumSnapshots, int maximumRows, int maximumBytes, TimeSpan lifetime)
            {
                MaximumSnapshots = maximumSnapshots;
                MaximumRows = maximumRows;
                MaximumBytes = maximumBytes;
                Lifetime = lifetime;
            }
        }
    }
}
