using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Threading;

namespace RimWorldDevBridge
{
    internal sealed class BridgeAuthorization
    {
        private const int LeaseSeconds = 120;
        private const int CompletedWriteLimit = 256;
        private const int AuditLimitBytes = 1024 * 1024;
        private readonly object gate = new object();
        private readonly object auditGate = new object();
        private readonly Dictionary<string, WriteLease> leases = new Dictionary<string, WriteLease>(StringComparer.Ordinal);
        private readonly Dictionary<string, CachedWrite> completed = new Dictionary<string, CachedWrite>(StringComparer.Ordinal);
        private readonly Queue<string> completionOrder = new Queue<string>();
        private string sessionId;

        internal string Context => Snapshot().WriteContext;

        internal BridgeSessionContextSnapshot Snapshot()
        {
            lock (gate)
            {
                DateTime now = DateTime.UtcNow;
                RemoveExpired(now);
                WriteLease effective = leases.Values.Where(item => item.ExpiresUtc > now &&
                    item.Context == "live-confirmed").OrderByDescending(item => item.AcquiredUtc).FirstOrDefault();
                if (effective == null)
                    effective = leases.Values.Where(item => item.ExpiresUtc > now && item.Context == "sandbox")
                        .OrderByDescending(item => item.AcquiredUtc).FirstOrDefault();
                if (effective == null)
                    return new BridgeSessionContextSnapshot(sessionId ?? "unknown", "none", false, false,
                        "none", null);
                bool representative = effective.Context == "live-confirmed";
                return new BridgeSessionContextSnapshot(effective.SessionId, effective.Context, representative,
                    true, effective.Context, effective.ExpiresUtc);
            }
        }

        internal void RotateSession(string newSessionId)
        {
            lock (gate)
            {
                sessionId = newSessionId;
                leases.Clear();
                completed.Clear();
                completionOrder.Clear();
            }
        }

        internal BridgeResult Acquire(string requestedContext, bool mutationEnabled)
        {
            if (!mutationEnabled)
                return BridgeResult.Fail(BridgeStatus.FORBIDDEN, "remote_mutation_disabled");
            string normalized = (requestedContext ?? string.Empty).Trim().ToLowerInvariant();
            if (normalized == "test") normalized = "sandbox";
            if (normalized != "sandbox" && normalized != "live-confirmed")
                return BridgeResult.Fail(BridgeStatus.INVALID_ARGUMENT, "invalid_write_context",
                    "Use sandbox or live-confirmed.");
            WriteLease lease;
            lock (gate)
            {
                DateTime now = DateTime.UtcNow;
                lease = new WriteLease
                {
                    Token = Guid.NewGuid().ToString("N"),
                    SessionId = sessionId,
                    Context = normalized,
                    AcquiredUtc = now,
                    ExpiresUtc = now.AddSeconds(LeaseSeconds)
                };
                leases[lease.Token] = lease;
                RemoveExpired(now);
            }
            return BridgeResult.Ok("core.writeLease")
                .Add("lease", lease.Token)
                .Add("context", lease.Context)
                .Add("expiresUtc", lease.ExpiresUtc.ToString("o"))
                .Warn(normalized == "live-confirmed" ? "Writes are authorized against a live save." : null);
        }

        internal BridgeResult Renew(string leaseToken, bool mutationEnabled)
        {
            if (!mutationEnabled)
                return BridgeResult.Fail(BridgeStatus.FORBIDDEN, "remote_mutation_disabled");
            lock (gate)
            {
                DateTime now = DateTime.UtcNow;
                RemoveExpired(now);
                if (string.IsNullOrWhiteSpace(leaseToken) || !leases.TryGetValue(leaseToken, out WriteLease lease) ||
                    lease.SessionId != sessionId || lease.ExpiresUtc <= now)
                    return BridgeResult.Fail(BridgeStatus.FORBIDDEN, "write_lease_required");
                lease.ExpiresUtc = now.AddSeconds(LeaseSeconds);
                return BridgeResult.Ok("core.writeLeaseRenewed")
                    .Add("lease", lease.Token).Add("context", lease.Context)
                    .Add("expiresUtc", lease.ExpiresUtc.ToString("o"));
            }
        }

        internal BridgeResult Revoke(string leaseToken)
        {
            lock (gate)
            {
                if (string.IsNullOrWhiteSpace(leaseToken) || !leases.Remove(leaseToken))
                    return BridgeResult.Fail(BridgeStatus.FORBIDDEN, "write_lease_required");
                return BridgeResult.Ok("core.writeLeaseRevoked").Add("lease", leaseToken);
            }
        }

        internal BridgeResult Authorize(BridgeRequest request, BridgeCommandDescriptor descriptor,
            string leaseToken, bool mutationEnabled)
        {
            if (descriptor.Mode == BridgeCommandMode.PureRead) return null;
            if (!mutationEnabled) return BridgeResult.Fail(BridgeStatus.FORBIDDEN, "remote_mutation_disabled");
            if (string.IsNullOrWhiteSpace(request.IdempotencyKey))
                return BridgeResult.Fail(BridgeStatus.INVALID_ARGUMENT, "idempotency_key_required");
            lock (gate)
            {
                RemoveExpired();
                if (string.IsNullOrWhiteSpace(leaseToken) || !leases.TryGetValue(leaseToken, out WriteLease lease) ||
                    lease.SessionId != sessionId || lease.ExpiresUtc <= DateTime.UtcNow)
                    return BridgeResult.Fail(BridgeStatus.FORBIDDEN, "write_lease_required");
                if (descriptor.Mode == BridgeCommandMode.PotentiallyDestructive && lease.Context != "sandbox")
                    return BridgeResult.Fail(BridgeStatus.FORBIDDEN, "destructive_write_requires_sandbox");
            }
            return null;
        }

        internal bool TryGetCompleted(BridgeRequest request, out BridgeResult result)
        {
            result = null;
            if (string.IsNullOrEmpty(request.IdempotencyKey)) return false;
            string key = request.SessionId + ":" + request.IdempotencyKey;
            lock (gate)
            {
                if (!completed.TryGetValue(key, out CachedWrite cached)) return false;
                if (!string.Equals(cached.Command, request.Command, StringComparison.Ordinal) ||
                    !string.Equals(cached.Argument, request.Argument, StringComparison.Ordinal))
                {
                    result = BridgeResult.Fail(BridgeStatus.INVALID_ARGUMENT, "idempotency_key_conflict");
                    return true;
                }
                result = cached.Result.CopyFor(request.RequestId);
                request.IdempotentReplay = true;
                result.Warn("idempotent replay; the original mutation was not executed again");
                return true;
            }
        }

        internal void Remember(BridgeRequest request, BridgeResult result)
        {
            if (request.Mode == BridgeCommandMode.PureRead || string.IsNullOrEmpty(request.IdempotencyKey) ||
                result == null) return;
            string key = request.SessionId + ":" + request.IdempotencyKey;
            lock (gate)
            {
                if (request.SessionId != sessionId) return;
                if (!completed.ContainsKey(key)) completionOrder.Enqueue(key);
                completed[key] = new CachedWrite
                {
                    Command = request.Command,
                    Argument = request.Argument,
                    Result = result.CopyFor(request.RequestId)
                };
                while (completionOrder.Count > CompletedWriteLimit)
                    completed.Remove(completionOrder.Dequeue());
            }
        }

        internal void Audit(BridgeRequest request, BridgeResult result)
        {
            if (request?.Mode == BridgeCommandMode.PureRead) return;
            string userRoot = BridgePaths.UserRoot;
            string auditPath = BridgePaths.AuditPath;
            string line = DateTime.UtcNow.ToString("o") + "|session=" + BridgeText.Clean(request.SessionId) +
                "|request=" + BridgeText.Clean(request.RequestId) + "|command=" +
                BridgeText.Clean(request.Command) + "|mode=" + request.Mode + "|status=" +
                (result?.Status.ToString() ?? "ERROR") + "|idempotency=" +
                BridgeText.Clean(request.IdempotencyKey) + "|mutation=" +
                BridgeText.Clean(result?.MutationSummary);
            ThreadPool.QueueUserWorkItem(_ => WriteAudit(line, userRoot, auditPath));
        }

        private void WriteAudit(string line, string userRoot, string auditPath)
        {
            try
            {
                lock (auditGate)
                {
                    Directory.CreateDirectory(userRoot);
                    RotateAuditIfNeeded(auditPath);
                    File.AppendAllLines(auditPath, new[] { line });
                }
            }
            catch { }
        }

        private void RemoveExpired()
        {
            RemoveExpired(DateTime.UtcNow);
        }

        private void RemoveExpired(DateTime now)
        {
            foreach (string key in leases.Where(pair => pair.Value.ExpiresUtc <= now)
                .Select(pair => pair.Key).ToList()) leases.Remove(key);
        }

        private static void RotateAuditIfNeeded(string auditPath)
        {
            if (!File.Exists(auditPath) || new FileInfo(auditPath).Length < AuditLimitBytes) return;
            string previous = auditPath + ".previous";
            if (File.Exists(previous)) File.Delete(previous);
            File.Move(auditPath, previous);
        }

        private sealed class WriteLease
        {
            internal string Token;
            internal string SessionId;
            internal string Context;
            internal DateTime AcquiredUtc;
            internal DateTime ExpiresUtc;
        }

        private sealed class CachedWrite
        {
            internal string Command;
            internal string Argument;
            internal BridgeResult Result;
        }
    }

    internal sealed class BridgeSessionContextSnapshot
    {
        internal readonly string SessionId;
        internal readonly string WriteContext;
        internal readonly bool RepresentativePlayerBehavior;
        internal readonly bool WriteLeaseActive;
        internal readonly string LeaseState;
        internal readonly DateTime? LeaseExpiresUtc;

        internal BridgeSessionContextSnapshot(string sessionId, string writeContext,
            bool representativePlayerBehavior, bool writeLeaseActive, string leaseState,
            DateTime? leaseExpiresUtc)
        {
            SessionId = sessionId;
            WriteContext = writeContext;
            RepresentativePlayerBehavior = representativePlayerBehavior;
            WriteLeaseActive = writeLeaseActive;
            LeaseState = leaseState;
            LeaseExpiresUtc = leaseExpiresUtc;
        }
    }

    internal static class BridgeResultCopy
    {
        internal static BridgeResult CopyFor(this BridgeResult source, string requestId)
        {
            source.EnforceBounds();
            BridgeResult copy = new BridgeResult
            {
                RequestId = requestId,
                SessionId = source.SessionId,
                Command = source.Command,
                Provider = source.Provider,
                ProviderVersion = source.ProviderVersion,
                Mode = source.Mode,
                Status = source.Status,
                Schema = source.Schema,
                SchemaVersion = source.SchemaVersion,
                QueueDelayMs = source.QueueDelayMs,
                PreparationMs = source.PreparationMs,
                ExecutionMs = source.ExecutionMs,
                MainThreadBudgetMs = source.MainThreadBudgetMs,
                MainThreadOverrun = source.MainThreadOverrun,
                MaxMainThreadStepMs = source.MaxMainThreadStepMs,
                CooperativeSteps = source.CooperativeSteps,
                NonCooperativeExecution = source.NonCooperativeExecution,
                TickBefore = source.TickBefore,
                TickAfter = source.TickAfter,
                Truncated = source.Truncated,
                ContinuationCursor = source.ContinuationCursor,
                MutationSummary = source.MutationSummary
            };
            foreach (BridgeField field in source.Data) copy.AddCopiedField(field);
            foreach (string line in source.Lines) copy.AddLine(line);
            foreach (string warning in source.Warnings) copy.Warn(warning);
            return copy;
        }
    }
}
