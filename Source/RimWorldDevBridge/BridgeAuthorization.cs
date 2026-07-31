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
        private string context = "unknown";

        internal string Context { get { lock (gate) return context; } }

        internal void RotateSession(string newSessionId)
        {
            lock (gate)
            {
                sessionId = newSessionId;
                context = "unknown";
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
                lease = new WriteLease
                {
                    Token = Guid.NewGuid().ToString("N"),
                    SessionId = sessionId,
                    Context = normalized,
                    ExpiresUtc = DateTime.UtcNow.AddSeconds(LeaseSeconds)
                };
                context = normalized;
                leases[lease.Token] = lease;
                RemoveExpired();
            }
            return BridgeResult.Ok("core.writeLease")
                .Add("lease", lease.Token)
                .Add("context", lease.Context)
                .Add("expiresUtc", lease.ExpiresUtc.ToString("o"))
                .Warn(normalized == "live-confirmed" ? "Writes are authorized against a live save." : null);
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
            string line = DateTime.UtcNow.ToString("o") + "|session=" + BridgeText.Clean(request.SessionId) +
                "|request=" + BridgeText.Clean(request.RequestId) + "|command=" +
                BridgeText.Clean(request.Command) + "|mode=" + request.Mode + "|status=" +
                (result?.Status.ToString() ?? "ERROR") + "|idempotency=" +
                BridgeText.Clean(request.IdempotencyKey) + "|mutation=" +
                BridgeText.Clean(result?.MutationSummary);
            ThreadPool.QueueUserWorkItem(_ => WriteAudit(line));
        }

        private void WriteAudit(string line)
        {
            try
            {
                lock (auditGate)
                {
                    Directory.CreateDirectory(BridgePaths.UserRoot);
                    RotateAuditIfNeeded();
                    File.AppendAllLines(BridgePaths.AuditPath, new[] { line });
                }
            }
            catch { }
        }

        private void RemoveExpired()
        {
            foreach (string key in leases.Where(pair => pair.Value.ExpiresUtc <= DateTime.UtcNow)
                .Select(pair => pair.Key).ToList()) leases.Remove(key);
        }

        private static void RotateAuditIfNeeded()
        {
            if (!File.Exists(BridgePaths.AuditPath) || new FileInfo(BridgePaths.AuditPath).Length < AuditLimitBytes) return;
            string previous = BridgePaths.AuditPath + ".previous";
            if (File.Exists(previous)) File.Delete(previous);
            File.Move(BridgePaths.AuditPath, previous);
        }

        private sealed class WriteLease
        {
            internal string Token;
            internal string SessionId;
            internal string Context;
            internal DateTime ExpiresUtc;
        }

        private sealed class CachedWrite
        {
            internal string Command;
            internal string Argument;
            internal BridgeResult Result;
        }
    }

    internal static class BridgeResultCopy
    {
        internal static BridgeResult CopyFor(this BridgeResult source, string requestId)
        {
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
                TickBefore = source.TickBefore,
                TickAfter = source.TickAfter,
                Truncated = source.Truncated,
                ContinuationCursor = source.ContinuationCursor,
                MutationSummary = source.MutationSummary
            };
            copy.Data.AddRange(source.Data.Select(field => new BridgeField(field.Name, field.Value)
                { ValueType = field.ValueType }));
            copy.Lines.AddRange(source.Lines);
            copy.Warnings.AddRange(source.Warnings);
            return copy;
        }
    }
}
