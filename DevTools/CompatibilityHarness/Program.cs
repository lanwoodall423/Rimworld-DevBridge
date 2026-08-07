using System;
using System.Collections.Generic;
using System.Linq;
using System.IO;
using System.Net.Sockets;
using System.Reflection;
using System.Security.Cryptography;
using System.Globalization;
using System.Runtime.Serialization.Json;
using System.Runtime.Serialization;
using System.Text;
using System.Threading;
using RimWorldDevBridge;

internal static class Program
{
    private static int passed;
    private static int failed;
    private static readonly string HarnessUserRoot = Path.Combine(Path.GetTempPath(),
        "RimWorldDevBridgeHarnessUser-" + Guid.NewGuid().ToString("N"));

    private static int Main()
    {
        Directory.CreateDirectory(HarnessUserRoot);
        BridgePaths.SetUserRootForTests(HarnessUserRoot);
        Run("legacy request", LegacyRequest);
        Run("structured request", StructuredRequest);
        Run("agent identity isolation", AgentIdentityIsolation);
        Run("runtime boundary characterization", RuntimeBoundaryCharacterization);
        Run("finalize init defers before owner adoption", FinalizeInitDefersBeforeOwnerAdoption);
        Run("diagnostic command registration characterization", DiagnosticCommandRegistrationCharacterization);
        Run("event journal concurrent access", EventJournalConcurrentAccess);
        Run("invalid timeout rejected", InvalidTimeoutRejected);
        Run("out of range timeout rejected", OutOfRangeTimeoutRejected);
        Run("malformed option rejected", MalformedOptionRejected);
        Run("request bytes bounded", RequestBytesBounded);
        Run("response bytes bounded", ResponseBytesBounded);
        Run("line bytes bounded", LineBytesBounded);
        Run("result collections bounded", ResultCollectionsBounded);
        Run("typed result fields", TypedResultFields);
        Run("json value truncation flagged", JsonValueTruncationFlagged);
        Run("semantic adapter status", SemanticAdapterStatus);
        Run("legacy valid metric remains OK", LegacyValidMetric);
        Run("cursor scope", CursorScope);
        Run("cursor fields scope", CursorFieldsScope);
        Run("query snapshot mutation stability", QuerySnapshotMutationStability);
        Run("query snapshot cursor validation", QuerySnapshotCursorValidation);
        Run("query snapshot cleanup", QuerySnapshotCleanup);
        Run("query snapshot size limits", QuerySnapshotSizeLimits);
        Run("query snapshot bounded stress", QuerySnapshotBoundedStress);
        Run("scheduler cancellation", SchedulerCancellation);
        Run("expired queued request never executes", ExpiredQueuedRequest);
        Run("scheduler stale session", SchedulerStaleSession);
        Run("scheduler queue capacity", SchedulerQueueCapacity);
        Run("fair per-agent scheduler", FairPerAgentScheduler);
        Run("shared runtime lane fairness", SharedRuntimeLaneFairness);
        Run("restart drain barrier", RestartDrainBarrier);
        Run("restart coordinator state machine", RestartCoordinatorStateMachineTest);
        Run("restart postcondition does not coalesce stale cycle", RestartPostconditionDoesNotCoalesceStaleCycle);
        Run("restart supersession and identity contract", RestartSupersessionAndIdentityContract);
        Run("session context transitions", SessionContextTransitions);
        Run("bridge indicator state transitions", BridgeIndicatorStateTransitions);
        Run("event-driven state publication", EventDrivenStatePublication);
        Run("status publication version guard", StatusPublicationVersionGuard);
        Run("remote mutation confirmation security", RemoteMutationConfirmationSecurity);
        Run("audit projection and redaction", AuditProjectionAndRedaction);
        Run("remote mutation settings fail closed", RemoteMutationSettingsFailClosed);
        Run("mutation confirmation prompt stages", MutationConfirmationPromptStages);
        Run("mutation identity boundaries", MutationIdentityBoundaries);
        Run("production pawn thing job snapshots", ProductionPawnThingJobSnapshots);
        Run("wake signal idempotence", WakeSignalIdempotence);
        Run("main thread dispatch queue", MainThreadDispatchQueue);
        Run("lifecycle queue coalescing", LifecycleQueueCoalescing);
        Run("main thread owner assertion", MainThreadOwnerAssertion);
        Run("main thread owner adoption", MainThreadOwnerAdoption);
        Run("worker game transition lifecycle", WorkerGameTransitionLifecycle);
        Run("cooperative scheduler yielding", CooperativeSchedulerYielding);
        Run("cooperative cancellation", CooperativeCancellation);
        Run("cooperative deadline", CooperativeDeadline);
        Run("large built-in query yields", LargeBuiltInQueryYields);
        Run("scheduler reconfiguration", SchedulerReconfiguration);
        Run("scheduler completion timing", SchedulerCompletionTiming);
        Run("in-flight cancellation", InFlightCancellation);
        Run("write lease and idempotency", WriteLeaseAndIdempotency);
        Run("idempotency conflict", IdempotencyConflict);
        Run("pre-execution failure not cached", PreExecutionFailureNotCached);
        Run("idempotency copy preserves bounds", IdempotencyCopyPreservesBounds);
        Run("adapter catalog concurrent readers", AdapterCatalogConcurrentReaders);
        Run("manifest adapter lifecycle", ManifestAdapterLifecycle);
        Run("owner adapter discovery and duplicate resolution", OwnerAdapterDiscoveryAndDuplicateResolution);
        string ownerAdapters = Environment.GetEnvironmentVariable("RIMWORLD_DEVBRIDGE_OWNER_ADAPTERS");
        if (!string.IsNullOrWhiteSpace(ownerAdapters))
            Run("owner packaged adapter integration", () => OwnerPackagedAdapterIntegration(ownerAdapters));
        Run("missing feature prerequisite blocked", MissingFeaturePrerequisiteBlocked);
        Run("typed feature assertions", TypedFeatureAssertions);
        Run("batch derives transitive write mode", BatchDerivesWriteMode);
        Run("macro derives transitive write mode", MacroDerivesWriteMode);
        Run("macro cycle quarantined", MacroCycleQuarantined);
        Run("manifest generation reuse rejected", ManifestGenerationReuseRejected);
        Run("slow legacy adapter circuit breaker", SlowLegacyAdapterCircuitBreaker);
        Run("production transport lifecycle", ProductionTransportLifecycle);
        Console.WriteLine("compatibility=" + (failed == 0 ? "PASS" : "FAIL") +
            " passed=" + passed + " failed=" + failed);
        int exitCode = failed == 0 ? 0 : 1;
        try { Directory.Delete(HarnessUserRoot, true); } catch { }
        return exitCode;
    }

    private static void LegacyRequest()
    {
        Check(BridgeProtocol.TryParse("abc|STATUS|", "s1", out BridgeRequest request, out _), "parse");
        Equal("abc", request.RequestId, "id");
        Equal("STATUS", request.Command, "command");
        Equal("s1", request.SessionId, "session");
        Equal("line", request.OutputFormat, "format");
    }

    private static void StructuredRequest()
    {
        string raw = "x|MODS|filter=core|session=s2&format=json&timeoutMs=900&allowExpensive=true";
        Check(BridgeProtocol.TryParse(raw, "s1", out BridgeRequest request, out _), "parse");
        Equal("s2", request.SessionId, "explicit session");
        Equal("json", request.OutputFormat, "json");
        Check(request.AllowExpensive, "expensive");
        Check(request.DeadlineUtc > DateTime.UtcNow.AddMilliseconds(700), "deadline");
    }

    private static void AgentIdentityIsolation()
    {
        Check(BridgeProtocol.TryParse("x|STATUS||agentId=agent-a&workspaceId=workspace-a", "s1",
            out BridgeRequest parsed, out _), "agent request parse");
        Equal("agent-a", parsed.AgentId, "agent id");
        Equal("workspace-a", parsed.WorkspaceId, "workspace id");
        Check(BridgeCommands.Describe("AGENT_CONTEXT") != null, "agent context command missing");

        BridgeAuthorization authorization = new BridgeAuthorization();
        authorization.RotateSession("agent-session");
        BridgeResult acquired = authorization.Acquire("sandbox", true, "agent-a");
        string token = FieldValue(acquired, "lease");
        BridgeCommandDescriptor descriptor = new BridgeCommandDescriptor
        {
            Name = "AGENT_TEST_MUTATION",
            Mode = BridgeCommandMode.Reversible
        };
        BridgeRequest other = Request("agent-b", "agent-session");
        other.AgentId = "agent-b";
        other.AuthToken = token;
        other.IdempotencyKey = "agent-b-write";
        Check(authorization.Authorize(other, descriptor, token, true)?.Status == BridgeStatus.FORBIDDEN,
            "another agent used the lease");
        Check(authorization.Renew(token, true, "agent-b").Status == BridgeStatus.FORBIDDEN,
            "another agent renewed the lease");
        Check(authorization.Revoke(token, "agent-b").Status == BridgeStatus.FORBIDDEN,
            "another agent revoked the lease");
        Check(authorization.Renew(token, true, "agent-a").Status == BridgeStatus.OK,
            "owner agent could not renew the lease");

        QueuedContext context = new QueuedContext();
        BridgeScheduler scheduler = new BridgeScheduler(_ => BridgeResult.Ok("agent.test"),
            (_, __) => { });
        scheduler.Configure(context, "agent-session", 8, 3);
        BridgeRequest queued = Request("agent-cancel", "agent-session");
        queued.AgentId = "agent-a";
        Check(scheduler.Enqueue(queued) == null, "agent request was not queued");
        Check(!scheduler.Cancel(queued.RequestId, "agent-b"), "another agent cancelled a request");
        Check(scheduler.Cancel(queued.RequestId, "agent-a"), "owner agent could not cancel its request");
    }

    private static void RuntimeBoundaryCharacterization()
    {
        BridgeRuntime.BridgeRuntimeStateSnapshot snapshot = BridgeRuntime.StateSnapshot;
        Check(snapshot != null, "runtime snapshot missing");
        Check(BridgeCommands.Describe("STATUS") != null, "status descriptor missing");
        Check(BridgeCommands.Describe("AGENT_CONTEXT") != null, "agent context descriptor missing");
        Check(snapshot.Context != null, "session context missing");
        BridgeResult context = BridgeRuntime.AddSessionContext(BridgeResult.Ok("test.characterization"), snapshot);
        Equal(snapshot.Context.SessionId, FieldValue(context, "session"), "shared session");
        Equal(snapshot.Context.WriteContext, FieldValue(context, "context"), "shared lease context");
        Equal(snapshot.MutationConfirmation.State, FieldValue(context, "mutationConfirmation"),
            "shared confirmation state");
        Equal(BridgeText.Invariant(snapshot.RemoteMutationEnabled),
            FieldValue(context, "remoteMutationEnabled"), "shared mutation setting");
    }

    private static void FinalizeInitDefersBeforeOwnerAdoption()
    {
        int callerThread = Thread.CurrentThread.ManagedThreadId;
        int deferredBefore = BridgeRuntime.FinalizeInitDeferredCountForTests;
        long initialSequence = BridgeRuntime.LifecycleSequenceForTests;
        Exception workerException = null;
        Thread worker = new Thread(() =>
        {
            try { BridgeRuntime.OnFinalizeInit(); }
            catch (Exception exception) { workerException = exception; }
        });
        worker.Start();
        Check(worker.Join(5000), "pre-adoption finalize worker did not finish");
        Check(workerException == null, "pre-adoption finalize escaped an exception");
        Check(BridgeRuntime.FinalizeInitDeferredCountForTests > deferredBefore,
            "pre-adoption finalize was not queued");
        Equal(0, BridgeRuntime.FinalizeInitExecutionThreadIdForTests,
            "pre-adoption finalize touched game state on the worker");

        // Make the queued request stale before the first authoritative Root.Update. Its inert
        // callback must be discarded while the transition callback remains sequence-aware.
        worker = new Thread(() => BridgeRuntime.OnGameChanging(null));
        worker.Start();
        Check(worker.Join(5000), "pre-adoption transition worker did not finish");
        Check(BridgeRuntime.LifecycleSequenceForTests > initialSequence,
            "pre-adoption transition did not advance the lifecycle sequence");
        BridgeRuntime.OnRootUpdate();
        Equal(0, BridgeRuntime.FinalizeInitExecutionThreadIdForTests,
            "stale finalize callback executed");

        // A current request is still deferred, then executes exactly once on the owner thread.
        long currentSequence = BridgeRuntime.LifecycleSequenceForTests;
        workerException = null;
        worker = new Thread(() =>
        {
            try { BridgeRuntime.OnFinalizeInit(); }
            catch (Exception exception) { workerException = exception; }
        });
        worker.Start();
        Check(worker.Join(5000), "current finalize worker did not finish");
        Check(workerException == null, "current finalize escaped an exception");
        BridgeRuntime.OnRootUpdate();
        Equal(callerThread, BridgeRuntime.FinalizeInitExecutionThreadIdForTests,
            "deferred finalize executed off the owner thread");
        Equal(currentSequence, BridgeRuntime.FinalizedLifecycleSequenceForTests,
            "current finalize did not publish its lifecycle sequence");

        int executionThread = BridgeRuntime.FinalizeInitExecutionThreadIdForTests;
        workerException = null;
        worker = new Thread(() =>
        {
            try { BridgeRuntime.OnFinalizeInit(); }
            catch (Exception exception) { workerException = exception; }
        });
        worker.Start();
        Check(worker.Join(5000), "duplicate finalize worker did not finish");
        Check(workerException == null, "duplicate finalize escaped an exception");
        BridgeRuntime.OnRootUpdate();
        Equal(executionThread, BridgeRuntime.FinalizeInitExecutionThreadIdForTests,
            "duplicate finalize ran more than once");
    }

    private static void DiagnosticCommandRegistrationCharacterization()
    {
        Dictionary<string, string> commands = new Dictionary<string, string>(StringComparer.Ordinal);
        BridgeDiagnostics.Register((name, description, mode, cost, requiresMap, argumentSchema) =>
        {
            Check(!commands.ContainsKey(name), "duplicate diagnostic command " + name);
            commands[name] = mode + "|" + cost + "|" + requiresMap + "|" + argumentSchema + "|" + description;
        });

        Equal(28, commands.Count, "diagnostic command count");
        Check(commands.ContainsKey("PAWNS") && commands.ContainsKey("THINGS") &&
            commands.ContainsKey("EVENTS") && commands.ContainsKey("LOAD_GAME"),
            "diagnostic command set changed");
        Check(commands["PAWNS"].StartsWith("PureRead|Normal|True|filter,limit,cursor,fields|",
            StringComparison.Ordinal), "paged pawn descriptor changed");
        Check(commands["SELECT"].StartsWith("UiOnly|Trivial|True|[session:map:]thingId|",
            StringComparison.Ordinal), "selection descriptor changed");
        Check(commands["LOAD_GAME"].StartsWith("PotentiallyDestructive|Simulation|False|name|",
            StringComparison.Ordinal), "load descriptor changed");
    }

    private static void EventJournalConcurrentAccess()
    {
        int failures = 0;
        Thread[] workers = Enumerable.Range(0, 8).Select(workerIndex => new Thread(() =>
        {
            try
            {
                for (int index = 0; index < 100; index++)
                {
                    BridgeEventJournal.Record("characterization-" + workerIndex,
                        "detail-" + index + "|secret=should-be-clean");
                    BridgeRequest request = Request("journal-" + workerIndex + "-" + index,
                        BridgeRuntime.SessionId);
                    request.Command = "EVENTS";
                    request.Argument = "filter=characterization-" + workerIndex + "&limit=8";
                    BridgeResult report = BridgeEventJournal.Report(request);
                    Check(report.Status == BridgeStatus.OK, "concurrent event report failed");
                }
            }
            catch { Interlocked.Increment(ref failures); }
        })).ToArray();

        foreach (Thread worker in workers) worker.Start();
        foreach (Thread worker in workers) Check(worker.Join(5000), "event journal worker did not finish");
        Equal(0, failures, "event journal concurrent access failures");

        BridgeRequest finalRequest = Request("journal-final", BridgeRuntime.SessionId);
        finalRequest.Command = "EVENTS";
        finalRequest.Argument = "filter=characterization&limit=512";
        BridgeResult finalReport = BridgeEventJournal.Report(finalRequest);
        Equal(BridgeStatus.OK, finalReport.Status, "event journal final report status");
        Check(finalReport.Lines.Count > 0 && finalReport.Lines.All(line => line.Contains("kind:characterization-")),
            "event journal report lost concurrent records");
        Check(finalReport.Lines.All(line => !line.Contains("|secret=should-be-clean")),
            "event journal did not clean record delimiters");
    }

    private static void InvalidTimeoutRejected()
    {
        Check(!BridgeProtocol.TryParse("x|STATUS||timeoutMs=abc", "s", out _, out BridgeResult failure),
            "malformed timeout accepted");
        Equal(BridgeStatus.INVALID_ARGUMENT, failure.Status, "status");
    }

    private static void OutOfRangeTimeoutRejected()
    {
        Check(!BridgeProtocol.TryParse("x|STATUS||timeoutMs=49", "s", out _, out BridgeResult failure),
            "short timeout accepted");
        Equal(BridgeStatus.INVALID_ARGUMENT, failure.Status, "status");
    }

    private static void MalformedOptionRejected()
    {
        Check(!BridgeProtocol.TryParse("x|STATUS||session=%", "s", out _, out BridgeResult failure),
            "malformed escape accepted");
        Equal(BridgeStatus.INVALID_ARGUMENT, failure.Status, "status");
    }

    private static void RequestBytesBounded()
    {
        string raw = "x|STATUS|" + new string('a', BridgeProtocol.MaxRequestBytes);
        Check(!BridgeProtocol.TryParse(raw, "s", out _, out BridgeResult failure), "oversize accepted");
        Equal(BridgeStatus.INVALID_ARGUMENT, failure.Status, "status");
    }

    private static void ResponseBytesBounded()
    {
        BridgeResult result = BridgeResult.Ok();
        result.RequestId = "x";
        for (int i = 0; i < 1000; i++) result.AddLine(i + "=" + new string('z', 500));
        string text = BridgeProtocol.SerializeLines(result);
        Check(Encoding.UTF8.GetByteCount(text) <= BridgeProtocol.MaxResponseBytes, "response too large");
        Check(text.Contains("truncated=true"), "missing truncation marker");
    }

    private static void LineBytesBounded()
    {
        BridgeResult result = BridgeResult.Ok().AddLine(new string('x', BridgeProtocol.MaxLineBytes * 2));
        string text = BridgeProtocol.SerializeLines(result);
        Check(text.Contains("truncated=true reason:lineBytes"), "line truncation marker missing");
        Check(text.Split('\n').All(line => Encoding.UTF8.GetByteCount(line) <= BridgeProtocol.MaxLineBytes),
            "serialized line too large");
    }

    private static void ResultCollectionsBounded()
    {
        BridgeResult result = BridgeResult.Ok();
        for (int i = 0; i < 2000; i++)
        {
            result.Add("field" + i, i);
            result.AddLine("line" + i);
            result.Warn("warning" + i);
        }
        Equal(512, result.Data.Count, "field bound");
        Equal(1024, result.Lines.Count, "line bound");
        Equal(64, result.Warnings.Count, "warning bound");
        Check(result.Truncated, "collection truncation not reported");
    }

    private static void TypedResultFields()
    {
        BridgeResult result = BridgeResult.Ok().Add("boolean", true).Add("integer", 3)
            .Add("number", 1.5d).Add("text", "value");
        Equal("boolean", result.Data[0].ValueType, "boolean type");
        Equal("integer", result.Data[1].ValueType, "integer type");
        Equal("number", result.Data[2].ValueType, "number type");
        Equal("string", result.Data[3].ValueType, "string type");
    }

    private static void JsonValueTruncationFlagged()
    {
        BridgeResult result = BridgeResult.Ok().Add("large", "small");
        result.Data[0].Value = new string('x', 20000);
        string json = BridgeProtocol.Serialize(result, "json");
        Check(result.Truncated, "result truncation flag missing");
        Check(json.Contains("\"truncated\":true"), "JSON truncation flag missing");
    }

    private static void SemanticAdapterStatus()
    {
        Equal(BridgeStatus.NOT_FOUND, BridgeResult.FromLegacy(new[] { "thing=not_found" }).Status, "not found");
        Equal(BridgeStatus.INVALID_ARGUMENT,
            BridgeResult.FromLegacy(new[] { "speed=invalid expected:0-4" }).Status, "invalid");
        Equal(BridgeStatus.UNAVAILABLE, BridgeResult.FromLegacy(new[] { "map=none" }).Status, "map");
        Equal(BridgeStatus.ERROR, BridgeResult.FromLegacy(new[] { "validation=FAIL reason:broken" }).Status,
            "failure token");
    }

    private static void CursorScope()
    {
        string cursor = BridgeCursor.Encode("s1", "MODS", "x", 50);
        Check(BridgeCursor.TryDecode(cursor, "s1", "MODS", "x", out int offset), "decode");
        Equal(50, offset, "offset");
        Check(!BridgeCursor.TryDecode(cursor, "s2", "MODS", "x", out _), "cross-session cursor");
    }

    private static void CursorFieldsScope()
    {
        BridgeQuery first = BridgeQuery.Parse("filter=x&fields=id,label&limit=1", "s", "THINGS",
            out BridgeResult failure);
        Check(failure == null, "first query");
        BridgeQuerySnapshot snapshot = CreateSnapshot("s", "THINGS", first.CursorScope, 1,
            new[] { 1, 2, 3 });
        string cursor = BridgeCursor.EncodeSnapshot("s", "THINGS", first.CursorScope, first.Ordering,
            snapshot.Id, snapshot.ExpiresUtc.Ticks, 1);
        BridgeQuery same = BridgeQuery.Parse("filter=x&fields=id,label&cursor=" +
            Uri.EscapeDataString(cursor), "s", "THINGS", out failure);
        Check(failure == null && same.Offset == 1, "same fields rejected");
        BridgeQuery changed = BridgeQuery.Parse("filter=x&fields=id&cursor=" +
            Uri.EscapeDataString(cursor), "s", "THINGS", out failure);
        Check(changed == null && failure.Status == BridgeStatus.INVALID_ARGUMENT &&
            FieldValue(failure, "error") == "cursor_filter_mismatch", "cursor allowed changed fields");
        BridgeQuery legacy = BridgeQuery.Parse("filter=x&fields=id,label&cursor=" +
            Uri.EscapeDataString(BridgeCursor.Encode("s", "THINGS", first.CursorScope, 1)),
            "s", "THINGS", out failure);
        Check(legacy == null && FieldValue(failure, "error") == "snapshot_cursor_required",
            "legacy offset cursor accepted for snapshot query");
        BridgeQuerySnapshotStore.Remove(snapshot.Id);
    }

    private static void QuerySnapshotMutationStability()
    {
        BridgeQuerySnapshotStore.ResetLimitsForTests();
        try
        {
            List<BridgeQuerySnapshotRow> live = new List<BridgeQuerySnapshotRow>
            {
                new BridgeQuerySnapshotRow(30, "id=30"), new BridgeQuerySnapshotRow(10, "id=10"),
                new BridgeQuerySnapshotRow(40, "id=40"), new BridgeQuerySnapshotRow(20, "id=20")
            };
            BridgeQuerySnapshot snapshot = CreateSnapshot("s", "PAWNS", "\nfields=", 7,
                live.Select(row => new BridgeQuerySnapshotRow(row.StableId, row.Line)).ToArray());
            string cursor = BridgeCursor.EncodeSnapshot("s", "PAWNS", "\nfields=", "thingId:asc", snapshot.Id,
                snapshot.ExpiresUtc.Ticks, 2);
            live.Clear();
            live.Add(new BridgeQuerySnapshotRow(5, "id=5"));
            live.Add(new BridgeQuerySnapshotRow(40, "id=changed"));

            Check(BridgeQuerySnapshotStore.TryGet("s", "PAWNS", "\nfields=", "thingId:asc", 7, snapshot.Id,
                snapshot.ExpiresUtc.Ticks, out BridgeQuerySnapshot page, out BridgeResult failure),
                "snapshot lookup");
            Equal(30, page.Rows[2].StableId, "stable page first id");
            Equal(40, page.Rows[3].StableId, "stable page second id");
            BridgeQuery parsed = BridgeQuery.Parse("limit=2&cursor=" + Uri.EscapeDataString(cursor), "s",
                "PAWNS", out failure);
            Check(failure == null && parsed.Offset == 2 && parsed.SnapshotId == snapshot.Id,
                "stable cursor parse");
        }
        finally { BridgeQuerySnapshotStore.ResetLimitsForTests(); }
    }

    private static void QuerySnapshotCursorValidation()
    {
        BridgeQuerySnapshotStore.ResetLimitsForTests();
        try
        {
            BridgeQuerySnapshot snapshot = CreateSnapshot("s", "THINGS", "x\nfields=id", 2,
                new[] { 1, 2 });
            string cursor = BridgeCursor.EncodeSnapshot("s", "THINGS", "x\nfields=id", "thingId:asc",
                snapshot.Id, snapshot.ExpiresUtc.Ticks, 1);
            BridgeQuery crossSession = BridgeQuery.Parse("filter=x&fields=id&cursor=" +
                Uri.EscapeDataString(cursor), "other", "THINGS", out BridgeResult failure);
            Check(crossSession == null && FieldValue(failure, "error") == "cursor_session_mismatch",
                "cross-session cursor accepted");
            BridgeQuery malformed = BridgeQuery.Parse("filter=x&fields=id&cursor=not-a-cursor", "s",
                "THINGS", out failure);
            Check(malformed == null && FieldValue(failure, "error") == "invalid_cursor", "malformed cursor accepted");
            string expired = BridgeCursor.EncodeSnapshot("s", "THINGS", "x\nfields=id", "thingId:asc",
                snapshot.Id, DateTime.UtcNow.AddSeconds(-1).Ticks, 1);
            BridgeQuery expiredQuery = BridgeQuery.Parse("filter=x&fields=id&cursor=" +
                Uri.EscapeDataString(expired), "s", "THINGS", out failure);
            Check(expiredQuery == null && FieldValue(failure, "error") == "cursor_expired",
                "expired cursor accepted");
            Check(!BridgeQuerySnapshotStore.TryGet("s", "THINGS", "x\nfields=id", "other", 2, snapshot.Id,
                snapshot.ExpiresUtc.Ticks, out _, out failure) &&
                FieldValue(failure, "error") == "cursor_order_mismatch", "order mismatch accepted");
        }
        finally { BridgeQuerySnapshotStore.ResetLimitsForTests(); }
    }

    private static void QuerySnapshotCleanup()
    {
        BridgeQuerySnapshotStore.ResetLimitsForTests();
        try
        {
            BridgeQuerySnapshot first = CreateSnapshot("s", "PAWNS", "", 1, new[] { 1 });
            BridgeQuerySnapshot second = CreateSnapshot("s", "THINGS", "", 2, new[] { 2 });
            BridgeQuerySnapshotStore.CleanupStaleMaps(new[] { 2 });
            Equal(1, BridgeQuerySnapshotStore.ActiveCount, "map cleanup count");
            Check(!BridgeQuerySnapshotStore.TryGet("s", "PAWNS", "", "thingId:asc", 1, first.Id,
                first.ExpiresUtc.Ticks, out _, out BridgeResult failure) &&
                FieldValue(failure, "error") == "cursor_snapshot_unavailable", "removed map snapshot retained");
            BridgeQuerySnapshotStore.RotateSession();
            Equal(0, BridgeQuerySnapshotStore.ActiveCount, "session cleanup count");
            BridgeQuerySnapshotStore.Remove(second.Id);

            BridgeQuerySnapshotStore.ConfigureLimitsForTests(4, 8, 100000,
                TimeSpan.FromMilliseconds(1));
            CreateSnapshot("s", "JOBS", "", 3, new[] { 1 });
            Thread.Sleep(10);
            CreateSnapshot("s", "JOBS", "", 3, new[] { 2 });
            Equal(1, BridgeQuerySnapshotStore.ActiveCount, "expired snapshot cleanup");
        }
        finally { BridgeQuerySnapshotStore.ResetLimitsForTests(); }
    }

    private static void QuerySnapshotSizeLimits()
    {
        try
        {
            BridgeQuerySnapshotStore.ConfigureLimitsForTests(1, 2, 100000, TimeSpan.FromMinutes(1));
            CreateSnapshot("s", "PAWNS", "", 1, new[] { 1, 2 });
            Check(!BridgeQuerySnapshotStore.TryCreate("s", "THINGS", "", "thingId:asc", 1, 1, false,
                Rows(3), out _, out BridgeResult failure) && FieldValue(failure, "error") == "snapshot_count_limit",
                "snapshot count limit ignored");
            BridgeQuerySnapshotStore.RotateSession();

            Check(!BridgeQuerySnapshotStore.TryCreate("s", "THINGS", "", "thingId:asc", 1, 3, false,
                Rows(1, 2, 3), out _, out failure) && FieldValue(failure, "error") == "snapshot_row_limit",
                "snapshot row limit ignored");
            BridgeQuerySnapshotStore.ConfigureLimitsForTests(1, 2, 50, TimeSpan.FromMinutes(1));
            Check(!BridgeQuerySnapshotStore.TryCreate("s", "THINGS", "", "thingId:asc", 1, 1, false,
                new[] { new BridgeQuerySnapshotRow(1, new string('x', 100)) }, out _, out failure) &&
                FieldValue(failure, "error") == "snapshot_memory_limit", "snapshot memory limit ignored");
        }
        finally { BridgeQuerySnapshotStore.ResetLimitsForTests(); }
    }

    private static void QuerySnapshotBoundedStress()
    {
        try
        {
            BridgeQuerySnapshotStore.ConfigureLimitsForTests(4, 64, 40000, TimeSpan.FromMinutes(1));
            int enumerated = 0;
            IEnumerable<BridgeQuerySnapshotRow> unbounded = Enumerable.Range(0, 100000).Select(index =>
            {
                enumerated++;
                return new BridgeQuerySnapshotRow(index, "id=" + index);
            });
            Check(!BridgeQuerySnapshotStore.TryCreate("s", "PAWNS", "", "thingId:asc", 1, 100000, false,
                unbounded, out _, out BridgeResult failure) && FieldValue(failure, "error") == "snapshot_row_limit",
                "unbounded source accepted");
            Equal(65, enumerated, "unbounded source work");
            for (int index = 0; index < 4; index++)
                Check(BridgeQuerySnapshotStore.TryCreate("s", "PAWNS", index.ToString(), "thingId:asc", 1,
                    64, false, Rows(Enumerable.Range(0, 64).ToArray()), out _, out failure),
                    "bounded snapshot " + index);
            Equal(4, BridgeQuerySnapshotStore.ActiveCount, "snapshot count bound");
            Check(BridgeQuerySnapshotStore.ActiveBytes <= 40000, "snapshot memory bound");
        }
        finally { BridgeQuerySnapshotStore.ResetLimitsForTests(); }
    }

    private static void SchedulerCancellation()
    {
        QueuedContext context = new QueuedContext();
        int executions = 0;
        BridgeScheduler scheduler = new BridgeScheduler(_ => { executions++; return BridgeResult.Ok(); });
        scheduler.Configure(context, "s", 8, 3);
        BridgeRequest request = Request("c", "s");
        Check(scheduler.Enqueue(request) == null, "enqueue");
        request.ClientDisconnected = true;
        context.Drain();
        Equal(0, executions, "executed disconnected request");
        Equal(BridgeStatus.CANCELLED, request.Result.Status, "cancel status");
    }

    private static void ExpiredQueuedRequest()
    {
        QueuedContext context = new QueuedContext();
        int executions = 0;
        BridgeScheduler scheduler = new BridgeScheduler(_ => { executions++; return BridgeResult.Ok(); });
        scheduler.Configure(context, "s", 8, 3);
        BridgeRequest request = Request("expired", "s");
        Check(scheduler.Enqueue(request) == null, "enqueue");
        request.DeadlineUtc = DateTime.UtcNow.AddMilliseconds(-1);
        context.Drain();
        Equal(0, executions, "expired request executed");
        Equal(BridgeStatus.TIMEOUT, request.Result.Status, "expired status");
    }

    private static void SchedulerStaleSession()
    {
        BridgeScheduler scheduler = new BridgeScheduler(_ => BridgeResult.Ok());
        scheduler.Configure(new QueuedContext(), "new", 8, 3);
        BridgeResult result = scheduler.Enqueue(Request("old", "old"));
        Equal(BridgeStatus.INCOMPATIBLE, result.Status, "stale status");
    }

    private static void SchedulerQueueCapacity()
    {
        BridgeScheduler scheduler = new BridgeScheduler(_ => BridgeResult.Ok());
        scheduler.Configure(new QueuedContext(), "s", 8, 3);
        for (int i = 0; i < 8; i++) Check(scheduler.Enqueue(Request(i.ToString(), "s")) == null, "fill " + i);
        Equal(BridgeStatus.BUSY, scheduler.Enqueue(Request("overflow", "s")).Status, "queue bound");
    }

    private static void FairPerAgentScheduler()
    {
        QueuedContext context = new QueuedContext();
        List<string> order = new List<string>();
        BridgeScheduler scheduler = new BridgeScheduler(request =>
        {
            order.Add(request.AgentId);
            return BridgeResult.Ok("fair.test");
        });
        scheduler.Configure(context, "fair-session", 8, 12);
        for (int index = 0; index < 3; index++)
        {
            BridgeRequest first = Request("fair-a-" + index, "fair-session");
            first.AgentId = "agent-a";
            BridgeRequest second = Request("fair-b-" + index, "fair-session");
            second.AgentId = "agent-b";
            Check(scheduler.Enqueue(first) == null, "agent-a request was rejected");
            Check(scheduler.Enqueue(second) == null, "agent-b request was rejected");
        }
        context.Drain();
        Equal(6, order.Count, "fair request count");
        Equal("agent-a", order[0], "round robin first");
        Equal("agent-b", order[1], "round robin second");
        Equal("agent-a", order[2], "round robin third");
        Equal("agent-b", order[3], "round robin fourth");
        Equal("agent-a", order[4], "round robin fifth");
        Equal("agent-b", order[5], "round robin sixth");
        Check(scheduler.Metrics().Lines.Any(line => line.Contains("agent-")), "redacted agent metrics missing");

        QueuedContext boundedContext = new QueuedContext();
        BridgeScheduler bounded = new BridgeScheduler(_ => BridgeResult.Ok("bounded.test"));
        bounded.Configure(boundedContext, "bounded-session", 64, 12);
        for (int index = 0; index < bounded.PerAgentQueueCapacity; index++)
        {
            BridgeRequest request = Request("bounded-" + index, "bounded-session");
            request.AgentId = "single-agent";
            Check(bounded.Enqueue(request) == null, "per-agent queue rejected within bound");
        }
        BridgeRequest overflow = Request("bounded-overflow", "bounded-session");
        overflow.AgentId = "single-agent";
        BridgeResult rejected = bounded.Enqueue(overflow);
        Check(rejected != null && rejected.Status == BridgeStatus.BUSY &&
            FieldValue(rejected, "error") == "agent_queue_full", "per-agent queue limit missing");
        bounded.RotateSession("bounded-next");
    }

    private static void SharedRuntimeLaneFairness()
    {
        QueuedContext context = new QueuedContext();
        List<string> order = new List<string>();
        BridgeScheduler scheduler = new BridgeScheduler(request =>
        {
            order.Add(request.Command);
            return BridgeResult.Ok("lane.test");
        });
        scheduler.Configure(context, "lane-session", 8, 12);

        BridgeRequest read = Request("lane-read", "lane-session");
        read.AgentId = "read-agent";
        read.Mode = BridgeCommandMode.PureRead;
        BridgeRequest mutation = Request("lane-write", "lane-session");
        mutation.AgentId = "write-agent";
        mutation.Command = "SET_SPEED";
        mutation.Mode = BridgeCommandMode.PersistentMutation;
        BridgeRequest readAgain = Request("lane-read-again", "lane-session");
        readAgain.AgentId = "read-agent-again";
        BridgeRequest mutationAgain = Request("lane-write-again", "lane-session");
        mutationAgain.AgentId = "write-agent-again";
        mutationAgain.Command = "SET_SPEED";
        mutationAgain.Mode = BridgeCommandMode.PersistentMutation;
        Check(scheduler.Enqueue(read) == null, "read lane request rejected");
        Check(scheduler.Enqueue(mutation) == null, "runtime lane request rejected");
        Check(scheduler.Enqueue(readAgain) == null, "second read lane request rejected");
        Check(scheduler.Enqueue(mutationAgain) == null, "second runtime lane request rejected");
        context.Drain();
        Check(order.Count == 4 && order[0] != order[1] && order[1] != order[2] && order[2] != order[3] &&
            order.All(command => command == "STATUS" || command == "SET_SPEED") &&
            order.Count(command => command == "STATUS") == 2 && order.Count(command => command == "SET_SPEED") == 2,
            "runtime lane did not alternate fairly with ordinary work: " + string.Join(",", order));
        BridgeResult metrics = scheduler.Metrics();
        Equal("2", FieldValue(metrics, "runtimeLaneAcquired"), "runtime lane acquisition metric");
        Equal("2", FieldValue(metrics, "runtimeLaneCompleted"), "runtime lane completion metric");
        Equal("0", FieldValue(metrics, "runtimeLanePending"), "runtime lane pending metric");
    }

    private static void RestartDrainBarrier()
    {
        QueuedContext context = new QueuedContext();
        BridgeScheduler scheduler = new BridgeScheduler(_ => BridgeResult.Ok("barrier.test"));
        scheduler.Configure(context, "barrier-session", 8, 12);
        BridgeRequest before = Request("barrier-before", "barrier-session");
        before.AgentId = "agent-a";
        Check(scheduler.Enqueue(before) == null, "pre-barrier request rejected");
        long barrier = scheduler.BeginDrain();
        Check(barrier > 0 && scheduler.IsDraining, "drain barrier was not established");
        BridgeRequest after = Request("barrier-after", "barrier-session");
        after.AgentId = "agent-b";
        Check(scheduler.Enqueue(after)?.Status == BridgeStatus.BUSY, "ordinary post-barrier work accepted");
        BridgeRequest heartbeat = Request("barrier-heartbeat", "barrier-session");
        heartbeat.Command = "RESTART_HEARTBEAT";
        Check(scheduler.Enqueue(heartbeat) == null, "coordinator heartbeat rejected");
        context.Drain();
        Check(scheduler.IsDrainComplete(), "pre-barrier work did not drain");
        Check(scheduler.DrainStatus().Status == BridgeStatus.OK, "drain status failed");
    }

    private static void RestartCoordinatorStateMachineTest()
    {
        BridgeRestartCoordinatorStateMachine machine = new BridgeRestartCoordinatorStateMachine();
        BridgeRestartTicketRecord first = machine.Request("agent-a", "owner.a", "gameplay change",
            "game", "none", "core-a", "adapter-a", false, false);
        BridgeRestartTicketRecord second = machine.Request("agent-b", "owner.b", "adapter change",
            "bridge", "none", "core-a", "adapter-a", false, false);
        Equal(first.CycleId, second.CycleId, "compatible restart requests were not coalesced");
        BridgeRestartTicketRecord incompatible = machine.Request("agent-c", "owner.c", "different core",
            "game", "none", "core-b", "adapter-a", false, false);
        Check(incompatible.CycleId != first.CycleId, "incompatible fingerprint joined existing cycle");
        BridgeRestartTicketRecord checkpoint = machine.Request("agent-c", "owner.c", "checkpoint",
            "game", "development-copy", "core-a", "adapter-a", true, false);
        Check(checkpoint.CycleId != first.CycleId, "incompatible save policy joined existing cycle");
        BridgeRestartTicketRecord live = machine.Request("agent-d", "owner.d", "live test",
            "game", "none", "core-a", "adapter-a", false, false, true);
        Equal(BridgeRestartPhase.FAILED.ToString(), live.Phase, "unauthorized live restart accepted");
        machine.SetPhase(first.CycleId, BridgeRestartPhase.DRAINING, "barrier");
        machine.SetPhase(first.CycleId, BridgeRestartPhase.DRAINED, "drained");
        machine.SetPhase(first.CycleId, BridgeRestartPhase.USER_RESTART_REQUIRED, "attached");
        Equal(BridgeRestartPhase.USER_RESTART_REQUIRED.ToString(), machine.Ticket(first.Ticket).Phase,
            "attached process did not require user restart");

        string root = Path.Combine(Path.GetTempPath(), "RimWorldDevBridgeCoordinatorTest-" + Guid.NewGuid().ToString("N"));
        string statePath = Path.Combine(root, "state.json");
        string secretPath = Path.Combine(root, "secret.txt");
        try
        {
            BridgeRestartCoordinatorStateMachine.WriteAtomic(statePath, machine.Snapshot);
            BridgeRestartCoordinatorState restored = BridgeRestartCoordinatorStateMachine.Read(statePath);
            Check(restored != null && restored.Tickets.Count >= 4, "coordinator state was not recoverable");
            Check(!string.IsNullOrEmpty(BridgeRestartCoordinatorStateMachine.Secret(secretPath)),
                "coordinator secret was not created");
        }
        finally { try { Directory.Delete(root, true); } catch { } }
    }

    private static void RestartPostconditionDoesNotCoalesceStaleCycle()
    {
        BridgeRestartCoordinatorStateMachine machine = new BridgeRestartCoordinatorStateMachine();
        BridgeRestartTicketRecord old = machine.Request("agent-old", "owner.a", "old assembly is loaded",
            "game", "none", "core-old", "adapter-old", true, false, false, true);
        machine.SetPhase(old.CycleId, BridgeRestartPhase.WAITING_FOR_GAME, "waiting for old game context");

        BridgeRestartTicketRecord replacement = machine.Request("agent-new", "owner.a",
            "new assembly requires replacement process", "game", "none", "core-old", "adapter-old",
            true, false, false, false);
        Check(replacement.CycleId != old.CycleId,
            "replacement postcondition incorrectly joined stale WAITING_FOR_GAME cycle");
    }

    private static void RestartSupersessionAndIdentityContract()
    {
        BridgeRestartCoordinatorStateMachine machine = new BridgeRestartCoordinatorStateMachine();
        BridgeRestartTicketRecord old = machine.Request("agent-old", "owner.a", "runtime verification",
            "game", "none", "core-old", "adapter-old", true, false, false, true,
            2, 500, "game", false, 4, "41", "session-old", false, 1000);
        machine.SetCycleIdentity(old.CycleId, "41", "boot-old", "session-old", 4);
        machine.SetPhase(old.CycleId, BridgeRestartPhase.WAITING_FOR_GAME, "waiting for context");

        BridgeRestartCoordinatorState stale = machine.Snapshot;
        stale.Cycles.Single(item => item.CycleId == old.CycleId).ProgressDeadlineUtc = DateTime.UtcNow.AddMilliseconds(-1);
        machine = new BridgeRestartCoordinatorStateMachine(stale);
        Check(machine.IsProgressExpired(old.CycleId, DateTime.UtcNow), "stale cycle watchdog did not expire");

        BridgeRestartTicketRecord replacement = machine.Request("agent-new", "owner.a",
            "new assembly requires replacement process", "game", "none", "core-new", "adapter-old",
            true, false, false, false, 2, 500, "game", true, 5, "42", "session-new", true, 1000);
        Check(replacement.CycleId != old.CycleId, "stale cycle was not superseded");
        Equal(replacement.CycleId, machine.Ticket(old.Ticket).CycleId,
            "old waiter did not move to replacement cycle");
        Equal(replacement.CycleId, machine.Ticket(old.Ticket).ReplacementCycleId,
            "old waiter did not retain replacement link");
        Check(replacement.RequiresNewProcess && replacement.RequestedPid == "42" &&
            replacement.RequestedSessionId == "session-new" && replacement.RequestedLifecycleGeneration == 5,
            "replacement identity contract was not persisted");
        Check(machine.Snapshot.Cycles.Single(item => item.CycleId == old.CycleId).Phase ==
            BridgeRestartPhase.FAILED.ToString(), "superseded cycle was not terminal");
    }

    private static void SessionContextTransitions()
    {
        InvokeRotateSession("context");
        string contextSession = BridgeRuntime.SessionContext.SessionId;
        FieldInfo authorizationField = typeof(BridgeRuntime).GetField("Authorization",
            BindingFlags.Static | BindingFlags.NonPublic);
        BridgeAuthorization authorization = (BridgeAuthorization)authorizationField.GetValue(null);

        AssertContext(BridgeRuntime.SessionContext, contextSession, "none", false, false, "none");
        authorization.Acquire("test", true);
        AssertContext(BridgeRuntime.SessionContext, contextSession, "sandbox", false, true, "sandbox");
        authorization.Acquire("live-confirmed", true);
        AssertContext(BridgeRuntime.SessionContext, contextSession, "live-confirmed", true, true, "live-confirmed");
        ExpireAllLeases(authorization);
        AssertContext(BridgeRuntime.SessionContext, contextSession, "none", false, false, "none");

        InvokeRotateSession("rotated");
        AssertContext(BridgeRuntime.SessionContext, BridgeRuntime.SessionContext.SessionId, "none", false, false,
            "none");
    }

    private static void WakeSignalIdempotence()
    {
        BridgeWakeSignal signal = new BridgeWakeSignal();
        Thread[] workers = Enumerable.Range(0, 8).Select(_ => new Thread(() =>
        {
            for (int i = 0; i < 100; i++) signal.Signal();
        })).ToArray();
        foreach (Thread worker in workers) worker.Start();
        foreach (Thread worker in workers) Check(worker.Join(5000), "wake signal worker did not finish");
        Check(signal.Consume(), "wake signal was lost");
        Check(!signal.Consume(), "wake signal was not coalesced");
        signal.Signal();
        Check(signal.Consume(), "wake signal was not reusable");
    }

    private static void MainThreadDispatchQueue()
    {
        BridgeMainThreadContext context = new BridgeMainThreadContext();
        int called = 0;
        Thread worker = new Thread(() => context.Post(_ => called++, null));
        worker.Start();
        Check(worker.Join(5000), "worker did not finish");
        Equal(0, called, "callback ran off main thread");
        Equal(1, context.Drain(16, 100), "callback not drained");
        Equal(1, called, "callback did not execute");
    }

    private static void LifecycleQueueCoalescing()
    {
        BridgeMainThreadContext context = new BridgeMainThreadContext();
        int executed = 0;
        long newest = -1;
        for (long sequence = 0; sequence < 10000; sequence++)
        {
            long captured = sequence;
            context.PostLifecycle(state =>
            {
                executed++;
                newest = (long)state;
            }, captured);
        }
        Equal(1, context.LifecyclePendingCount, "lifecycle queue was not bounded");
        Check(context.LifecycleCoalescedCount > 0, "lifecycle flood was not coalesced");
        Equal(1, context.DrainLifecycle(8, 100), "coalesced lifecycle callback was not drained");
        Equal(1, executed, "coalesced lifecycle callbacks executed more than once");
        Equal(9999L, newest, "newest lifecycle sequence was not preserved");

        context.PostLifecycle(state => executed++, 9998L);
        Check(context.LifecycleDroppedStaleCount > 0, "stale lifecycle callback was not dropped");
        Equal(0, context.DrainLifecycle(8, 100), "stale lifecycle callback was drained");

        BridgeResult metrics = BridgeRuntime.SchedulerMetrics();
        Check(FieldValue(metrics, "lifecyclePending") != null &&
            FieldValue(metrics, "lifecycleCoalesced") != null &&
            FieldValue(metrics, "lifecycleDroppedStale") != null,
            "lifecycle metrics were not projected");
    }

    private static void MainThreadOwnerAssertion()
    {
        BridgeMainThreadContext context = new BridgeMainThreadContext();
        bool rejected = false;
        Thread worker = new Thread(() =>
        {
            try { context.AssertOwnerThread("test worker"); }
            catch (InvalidOperationException) { rejected = true; }
        });
        worker.Start();
        Check(worker.Join(5000), "owner assertion worker did not finish");
        Check(rejected, "worker was treated as the game thread");
        bool callbackOnOwner = false;
        context.Post(_ =>
        {
            context.AssertOwnerThread("test callback");
            callbackOnOwner = true;
        }, null);
        Equal(1, context.Drain(16, 100), "owner callback was not drained");
        Check(callbackOnOwner, "owner callback did not execute");
    }

    private static void MainThreadOwnerAdoption()
    {
        BridgeMainThreadContext context = null;
        Thread loader = new Thread(() => context = new BridgeMainThreadContext());
        loader.Start();
        Check(loader.Join(5000), "loader did not finish");
        Check(context != null && !context.IsOwnerThread, "loader thread was unexpectedly the test owner");
        Check(context.AdoptOwnerThread(), "first authoritative callback did not adopt owner");
        context.AssertOwnerThread("adopted callback");

        bool workerAdopted = true;
        Thread worker = new Thread(() =>
        {
            workerAdopted = context.AdoptOwnerThread();
        });
        worker.Start();
        Check(worker.Join(5000), "post-adoption worker did not finish");
        Check(context.IsOwnerThread && !workerAdopted, "post-adoption worker adopted ownership");
        bool workerRejected = false;
        worker = new Thread(() =>
        {
            try { context.AssertOwnerThread("post-adoption worker"); }
            catch (InvalidOperationException) { workerRejected = true; }
        });
        worker.Start();
        Check(worker.Join(5000), "post-adoption assertion worker did not finish");
        Check(workerRejected, "post-adoption worker passed owner assertion");
    }

    private static void WorkerGameTransitionLifecycle()
    {
        BridgeRuntime.OnRootUpdate();
        InvokeRotateSession("lifecycle-test");
        FieldInfo authorizationField = typeof(BridgeRuntime).GetField("Authorization",
            BindingFlags.Static | BindingFlags.NonPublic);
        BridgeAuthorization authorization = (BridgeAuthorization)authorizationField.GetValue(null);
        authorization.Acquire("sandbox", true);
        string oldSession = BridgeRuntime.SessionId;

        BridgeQuerySnapshotStore.TryCreate(oldSession, "PAWNS", "lifecycle", "thingId:asc", 1, 1,
            false, Rows(new[] { 1 }), out _, out BridgeResult snapshotFailure);
        Check(snapshotFailure == null && BridgeQuerySnapshotStore.ActiveCount > 0,
            "lifecycle snapshot setup");
        FieldInfo schedulerField = typeof(BridgeRuntime).GetField("Scheduler",
            BindingFlags.Static | BindingFlags.NonPublic);
        BridgeScheduler scheduler = (BridgeScheduler)schedulerField.GetValue(null);
        BridgeRequest queued = Request("lifecycle-queued", oldSession);
        Check(scheduler.Enqueue(queued) == null, "lifecycle queued setup");

        long publishedBefore = BridgeRuntime.PublishedLifecycleSequenceForTests;
        Exception workerException = null;
        Thread worker = new Thread(() =>
        {
            try { BridgeRuntime.OnGameChanging(null); }
            catch (Exception exception) { workerException = exception; }
        });
        worker.Start();
        Check(worker.Join(5000), "null transition worker did not finish");
        Check(workerException == null, "null transition escaped exception");
        long nullSequence = BridgeRuntime.LifecycleSequenceForTests;
        Check(BridgeRuntime.SessionId != oldSession, "null transition did not rotate session");
        AssertContext(BridgeRuntime.SessionContext, BridgeRuntime.SessionId, "none", false, false, "none");
        Equal(0, BridgeQuerySnapshotStore.ActiveCount, "null transition snapshot cleanup");
        Check(queued.Done.IsSet && queued.Result.Status == BridgeStatus.CANCELLED,
            "old queued request retained authority");
        Equal(publishedBefore, BridgeRuntime.PublishedLifecycleSequenceForTests,
            "lifecycle publication was not deferred");

        BridgeRuntime.OnRootUpdate();
        Equal(nullSequence, BridgeRuntime.PublishedLifecycleSequenceForTests,
            "null transition was not published");
        Equal(Thread.CurrentThread.ManagedThreadId, BridgeRuntime.PublishedLifecycleThreadIdForTests,
            "lifecycle publication ran off owner thread");

        authorization.Acquire("live-confirmed", true);
        string beforeReplacement = BridgeRuntime.SessionId;
        Verse.Game replacement = (Verse.Game)System.Runtime.Serialization.FormatterServices
            .GetUninitializedObject(typeof(Verse.Game));
        workerException = null;
        worker = new Thread(() =>
        {
            try { BridgeRuntime.OnGameChanging(replacement); }
            catch (Exception exception) { workerException = exception; }
        });
        worker.Start();
        Check(worker.Join(5000), "replacement transition worker did not finish");
        Check(workerException == null, "replacement transition escaped exception");
        long replacementSequence = BridgeRuntime.LifecycleSequenceForTests;
        Check(BridgeRuntime.SessionId != beforeReplacement, "replacement did not rotate session");
        AssertContext(BridgeRuntime.SessionContext, BridgeRuntime.SessionId, "none", false, false, "none");

        // Two rapid transitions enqueue two callbacks; the first must be discarded by sequence.
        worker = new Thread(() => BridgeRuntime.OnGameChanging(null));
        worker.Start();
        Check(worker.Join(5000), "rapid null transition worker did not finish");
        worker = new Thread(() => BridgeRuntime.OnGameChanging(replacement));
        worker.Start();
        Check(worker.Join(5000), "rapid replacement transition worker did not finish");
        long newestSequence = BridgeRuntime.LifecycleSequenceForTests;
        Check(newestSequence > replacementSequence, "rapid transition sequence did not advance");
        BridgeRuntime.OnRootUpdate();
        Equal(newestSequence, BridgeRuntime.PublishedLifecycleSequenceForTests,
            "older lifecycle callback published after newer transition");
        Equal(Thread.CurrentThread.ManagedThreadId, BridgeRuntime.PublishedLifecycleThreadIdForTests,
            "rapid lifecycle publication ran off owner thread");
    }

    private static void ProductionTransportLifecycle()
    {
        BridgePaths.Initialize(AppDomain.CurrentDomain.BaseDirectory);
        BridgeRuntime.CaptureStatusPathForTests();
        Check(!BridgeRuntime.AuthenticateForTests("|STATUS|", string.Empty, out _),
            "empty expected token authenticated");
        Check(!BridgeRuntime.AuthenticateForTests("|STATUS|", "token", out _),
            "bare delimiter authenticated in parser");
        Check(BridgeRuntime.AuthenticateForTests("token|STATUS|", "token", out string payload) &&
            payload == "STATUS|", "valid token parser rejected");
        BridgeRuntime.OnRootUpdate();
        BridgeRuntime.SignalWakeForTests();
        BridgeRuntime.OnRootUpdate();
        Check(BridgeRuntime.Active, "wake did not activate transport");
        int oldGeneration = BridgeRuntime.TransportGenerationForTests;
        Equal(oldGeneration, BridgeRuntime.TransportResourceGenerationForTests,
            "active transport generation mismatch");
        int port = BridgeRuntime.TransportPortForTests;
        Check(port > 0, "active transport has no port");
        Check(SendUnauthenticatedRequest(port).Contains("authentication_failed"),
            "bare delimiter authenticated");

        Exception workerException = null;
        Thread worker = new Thread(() =>
        {
            try { BridgeRuntime.OnGameChanging(null); }
            catch (Exception exception) { workerException = exception; }
        });
        worker.Start();
        Check(worker.Join(5000), "active transition worker did not finish");
        Check(workerException == null, "active transition escaped exception");
        Check(!BridgeRuntime.Active, "transition left transport active");
        Equal(0, BridgeRuntime.TransportResourceGenerationForTests,
            "transition left transport resources attached");
        Check(!File.Exists(BridgePaths.StatusPath), "transition left stale status file");
        Check(!CanConnect(port), "old listener accepted after invalidation");

        int transitionSequence = (int)BridgeRuntime.LifecycleSequenceForTests;
        BridgeRuntime.SignalWakeForTests();
        BridgeRuntime.OnRootUpdate();
        Check(BridgeRuntime.Active, "immediate wake did not reactivate transport");
        int newGeneration = BridgeRuntime.TransportGenerationForTests;
        Check(newGeneration > oldGeneration, "transport generation did not advance");
        Equal(newGeneration, BridgeRuntime.TransportResourceGenerationForTests,
            "reactivated transport generation mismatch");
        Check(BridgeRuntime.PublishedLifecycleSequenceForTests != transitionSequence,
            "stale transition callback published after reactivation");
        Check(SendUnauthenticatedRequest(BridgeRuntime.TransportPortForTests)
            .Contains("authentication_failed"), "reactivated empty-token authentication changed");
        Check(!File.ReadAllText(BridgePaths.StatusPath).Contains("bridge=DORMANT"),
            "stale transition overwrote active status");

        workerException = null;
        worker = new Thread(() =>
        {
            try
            {
                Verse.Game replacement = (Verse.Game)System.Runtime.Serialization.FormatterServices
                    .GetUninitializedObject(typeof(Verse.Game));
                BridgeRuntime.OnGameChanging(replacement);
            }
            catch (Exception exception) { workerException = exception; }
        });
        worker.Start();
        Check(worker.Join(5000), "replacement transition worker did not finish");
        Check(workerException == null, "replacement transition escaped exception");
        Check(!BridgeRuntime.Active, "replacement transition left transport active");
        BridgeRuntime.OnRootUpdate();
        Equal(0, BridgeRuntime.TransportResourceGenerationForTests,
            "replacement cleanup did not run through root update");
    }

    private static string SendUnauthenticatedRequest(int port)
    {
        using (System.Net.Sockets.TcpClient client = new System.Net.Sockets.TcpClient())
        {
            client.Connect("127.0.0.1", port);
            using (NetworkStream stream = client.GetStream())
            {
                stream.ReadTimeout = 3000;
                byte[] request = Encoding.UTF8.GetBytes("|STATUS|\n");
                stream.Write(request, 0, request.Length);
                using (StreamReader reader = new StreamReader(stream, Encoding.UTF8, false, 1024, true))
                    return reader.ReadToEnd();
            }
        }
    }

    private static bool CanConnect(int port)
    {
        try
        {
            using (System.Net.Sockets.TcpClient client = new System.Net.Sockets.TcpClient())
            {
                client.Connect("127.0.0.1", port);
                return true;
            }
        }
        catch (SocketException) { return false; }
        catch (InvalidOperationException) { return false; }
    }

    private static void BridgeIndicatorStateTransitions()
    {
        BridgeIndicatorState hidden = BridgeIndicatorState.Create(false, 0, 8, null, false);
        Equal(BridgeIndicatorMode.Hidden, hidden.Mode, "hidden mode");
        Check(!hidden.Visible, "inactive read-only indicator visible");

        BridgeIndicatorState readOnly = BridgeIndicatorState.Create(true, 2, 8, null, false);
        Equal(BridgeIndicatorMode.ReadOnly, readOnly.Mode, "read-only mode");
        Check(readOnly.Visible, "active read-only indicator hidden");
        Check(readOnly.CompactDetails(DateTime.UtcNow).Contains("clients:2/8"), "client details missing");

        BridgeSessionContextSnapshot sandbox = new BridgeSessionContextSnapshot("s", "sandbox", false,
            true, "sandbox", DateTime.UtcNow.AddSeconds(30));
        BridgeIndicatorState sandboxState = BridgeIndicatorState.Create(false, 0, 8, sandbox, false);
        Equal(BridgeIndicatorMode.Sandbox, sandboxState.Mode, "sandbox mode");
        Check(sandboxState.Visible && sandboxState.Tooltip(DateTime.UtcNow).Contains("sandbox"),
            "sandbox lease was hidden");

        BridgeSessionContextSnapshot live = new BridgeSessionContextSnapshot("s", "live-confirmed", true,
            true, "live-confirmed", DateTime.UtcNow.AddSeconds(30));
        BridgeIndicatorState liveState = BridgeIndicatorState.Create(false, 0, 8, live, false);
        Equal(BridgeIndicatorMode.LiveConfirmed, liveState.Mode, "live mode");
        Check(liveState.Visible, "live lease became invisible");
        Check(liveState.Label.Contains("LIVE-CONFIRMED") &&
            liveState.Tooltip(DateTime.UtcNow).Contains("LIVE-CONFIRMED writes"), "live warning missing");
    }

    private static void EventDrivenStatePublication()
    {
        InvokeRotateSession("event-driven");
        BridgeRuntime.OnRootUpdate();
        BridgeRuntime.ResetStatePublicationCountersForTests();
        for (int i = 0; i < 8; i++) BridgeRuntime.OnRootUpdate();
        Equal(0, BridgeRuntime.StatusWriteCountForTests, "dormant status was recomputed every frame");
        Equal(0, BridgeRuntime.IndicatorRefreshCountForTests, "dormant indicator was refreshed every frame");

        WithTestGame(true, () =>
        {
            BridgeRuntime.ConfirmMutationForCurrentGame();
            BridgeResult acquired = BridgeRuntime.AcquireWriteLease("sandbox");
            Check(acquired.Status == BridgeStatus.OK, "lease acquisition failed");
            int writesAfterAcquire = BridgeRuntime.StatusWriteCountForTests;
            Check(writesAfterAcquire > 0, "lease acquisition did not publish status");
            Check(BridgeRuntime.IndicatorRefreshCountForTests > 0, "lease acquisition did not refresh indicator");
            string leaseToken = FieldValue(acquired, "lease");
            BridgeResult renewed = BridgeRuntime.RenewWriteLease(leaseToken);
            Check(renewed.Status == BridgeStatus.OK, "lease renewal failed");
            Check(BridgeRuntime.StatusWriteCountForTests > writesAfterAcquire,
                "lease renewal did not publish status");

            FieldInfo authorizationField = typeof(BridgeRuntime).GetField("Authorization",
                BindingFlags.Static | BindingFlags.NonPublic);
            ExpireAllLeases((BridgeAuthorization)authorizationField.GetValue(null));
            FieldInfo expiryField = typeof(BridgeRuntime).GetField("leaseExpiryTicks",
                BindingFlags.Static | BindingFlags.NonPublic);
            expiryField.SetValue(null, DateTime.UtcNow.AddSeconds(-1).Ticks);
            BridgeRuntime.OnRootUpdate();
            Check(!BridgeRuntime.SessionContext.WriteLeaseActive, "expired lease remained active");
            Check(BridgeRuntime.StatusWriteCountForTests > writesAfterAcquire,
                "lease expiration did not publish status");
            int writesAfterExpiry = BridgeRuntime.StatusWriteCountForTests;
            BridgeRuntime.OnRootUpdate();
            Equal(writesAfterExpiry, BridgeRuntime.StatusWriteCountForTests,
                "post-expiry dormant status kept publishing");
            BridgeResult reacquired = BridgeRuntime.AcquireWriteLease("sandbox");
            BridgeResult revoked = BridgeRuntime.RevokeWriteLease(FieldValue(reacquired, "lease"));
            Check(revoked.Status == BridgeStatus.OK && !BridgeRuntime.SessionContext.WriteLeaseActive,
                "lease revocation did not invalidate authority");
        });
        InvokeRotateSession("event-driven-cleanup");
    }

    private static void StatusPublicationVersionGuard()
    {
        BridgeRuntime.BridgeRuntimeStateSnapshot snapshot = BridgeRuntime.StateSnapshot;
        int writes = BridgeRuntime.StatusWriteCountForTests;
        BridgeStatusPublication publication = new BridgeStatusPublication(snapshot, "DORMANT", null,
            BridgeRuntime.BootstrapMs, BridgeRuntime.HarmonyMs,
            BridgeRuntime.FinalizeInitMs, BridgeRuntime.ActivationMs,
            BridgeRuntime.BootstrapManagedDeltaBytes);
        Check(!BridgeStatusPublisher.Write(publication, () => snapshot.Version + 1),
            "stale status snapshot was accepted");
        Equal(writes, BridgeRuntime.StatusWriteCountForTests,
            "stale status snapshot changed write metrics");
    }

    private static void RemoteMutationConfirmationSecurity()
    {
        InvokeRotateSession("mutation-security");
        WithTestGame(false, () =>
        {
            BridgeResult disabled = BridgeRuntime.AcquireWriteLease("sandbox", "malicious-agent");
            Equal("remote_mutation_disabled", FieldValue(disabled, "error"),
                "default-disabled mutation lease error");
            Check(!BridgeRuntime.SessionContext.WriteLeaseActive, "disabled setting issued a lease");
        });

        WithTestGame(true, () =>
        {
            BridgeResult malicious = BridgeRuntime.AcquireWriteLease("sandbox", "malicious-agent");
            Equal("in_game_confirmation_required", FieldValue(malicious, "error"),
                "sandbox label was treated as confirmation");
            BridgeResult confirmed = BridgeRuntime.ConfirmMutationForCurrentGame();
            Check(confirmed.Status == BridgeStatus.OK, "in-game confirmation was not accepted");
            BridgeResult acquired = BridgeRuntime.AcquireWriteLease("sandbox", "agent-a");
            Check(acquired.Status == BridgeStatus.OK, "confirmed lease was not issued");
            string token = FieldValue(acquired, "lease");
            Equal("write_lease_agent_mismatch",
                FieldValue(BridgeRuntime.RenewWriteLease(token, "agent-b"), "error"),
                "wrong agent renewed lease");
            Equal("write_lease_agent_mismatch",
                FieldValue(BridgeRuntime.RevokeWriteLease(token, "agent-b"), "error"),
                "wrong agent revoked lease");
            BridgeResult revoked = BridgeRuntime.RevokeMutationConfirmation();
            Check(revoked.Status == BridgeStatus.OK && !BridgeRuntime.SessionContext.WriteLeaseActive,
                "confirmation revocation did not clear the lease");
            Equal("in_game_confirmation_required",
                FieldValue(BridgeRuntime.AcquireWriteLease("sandbox", "agent-a"), "error"),
                "lease was issued after confirmation revocation");

            BridgeResult auditRequestResult = BridgeRuntime.ConfirmMutationForCurrentGame();
            Check(auditRequestResult.Status == BridgeStatus.OK, "audit confirmation setup failed");
            BridgeResult auditLease = BridgeRuntime.AcquireWriteLease("sandbox", "agent-a");
            BridgeRequest auditRequest = Request("audit-security", BridgeRuntime.SessionId);
            auditRequest.Command = "SET_SPEED";
            auditRequest.Mode = BridgeCommandMode.Reversible;
            auditRequest.IdempotencyKey = "audit-security-key";
            auditRequest.AuthToken = "secret-token";
            auditRequest.MutationGameIdentity = "game-audit";
            auditRequest.MutationSaveIdentity = "save-audit";
            auditRequest.MutationGameLoaded = true;
            auditRequest.MutationSettingEnabled = true;
            auditRequest.MutationConfirmationState = "confirmed";
            auditRequest.AuthorizedLeaseContext = FieldValue(auditLease, "context");
            FieldInfo authorizationField = typeof(BridgeRuntime).GetField("Authorization",
                BindingFlags.Static | BindingFlags.NonPublic);
            ((BridgeAuthorization)authorizationField.GetValue(null)).Audit(auditRequest, BridgeResult.Ok());
            Thread.Sleep(100);
            string audit = File.Exists(BridgePaths.AuditPath) ? File.ReadAllText(BridgePaths.AuditPath) : string.Empty;
            Check(audit.Contains("gameLoaded=true") && audit.Contains("confirmation=confirmed") &&
                audit.Contains("leaseContext=sandbox") && audit.Contains("saveIdentity=save-audit"),
                "audit omitted server authorization state");
            Check(!audit.Contains("secret-token"), "audit leaked a transport/lease token");

            Exception workerError = null;
            Thread worker = new Thread(() =>
            {
                try { BridgeRuntime.OnGameChanging(null); } catch (Exception error) { workerError = error; }
            });
            worker.Start();
            worker.Join();
            Check(workerError == null && !BridgeRuntime.SessionContext.WriteLeaseActive &&
                !BridgeRuntime.StateSnapshot.MutationConfirmation.Confirmed,
                "game transition did not immediately invalidate mutation authority");
        });

        FieldInfo currentGameField = typeof(Verse.Current).GetField("gameInt",
            BindingFlags.Static | BindingFlags.NonPublic);
        object previousGame = currentGameField.GetValue(null);
        BridgeSettings previousSettings = RimWorldDevBridgeMod.Settings;
        try
        {
            RimWorldDevBridgeMod.Settings = new BridgeSettings { RemoteMutationEnabled = true };
            currentGameField.SetValue(null, null);
            BridgeRuntime.ApplyRemoteMutationSettings();
            Equal("no_game_loaded", FieldValue(BridgeRuntime.AcquireWriteLease("sandbox", "agent-a"), "error"),
                "no-game mutation lease error");
        }
        finally
        {
            currentGameField.SetValue(null, previousGame);
            RimWorldDevBridgeMod.Settings = previousSettings;
            BridgeRuntime.ApplyRemoteMutationSettings();
            InvokeRotateSession("mutation-security-cleanup");
        }
    }

    private static void RemoteMutationSettingsFailClosed()
    {
        BridgeSettings previousSettings = RimWorldDevBridgeMod.Settings;
        try
        {
            RimWorldDevBridgeMod.Settings = null;
            BridgeRuntime.ApplyRemoteMutationSettings();
            Check(!BridgeRuntime.StateSnapshot.RemoteMutationEnabled,
                "unavailable settings enabled remote mutation");
            Equal("remote_mutation_disabled",
                FieldValue(BridgeRuntime.AcquireWriteLease("sandbox", "settings-unavailable"), "error"),
                "unavailable settings lease error");

            RimWorldDevBridgeMod.Settings = new BridgeSettings();
            BridgeRuntime.ApplyRemoteMutationSettings();
            Check(!BridgeRuntime.StateSnapshot.RemoteMutationEnabled,
                "uninitialized settings enabled remote mutation");
            Equal("remote_mutation_disabled",
                FieldValue(BridgeRuntime.AcquireWriteLease("sandbox", "settings-uninitialized"), "error"),
                "uninitialized settings lease error");
        }
        finally
        {
            RimWorldDevBridgeMod.Settings = previousSettings;
            BridgeRuntime.ApplyRemoteMutationSettings();
            InvokeRotateSession("settings-fail-closed-cleanup");
        }
    }

    private static void AuditProjectionAndRedaction()
    {
        BridgeAuthorization authorization = new BridgeAuthorization();
        authorization.RotateSession("audit-characterization-session");
        string marker = "audit-characterization-" + Guid.NewGuid().ToString("N");
        BridgeRequest request = Request(marker, "audit-characterization-session");
        request.Command = "AUDIT_PROJECTION";
        request.Mode = BridgeCommandMode.Reversible;
        request.IdempotencyKey = marker + "-key";
        request.AuthToken = "secret-transport-token-" + marker;
        request.MutationGameLoaded = true;
        request.MutationGameIdentity = "game|identity\r\n";
        request.MutationSaveIdentity = "save-digest";
        request.MutationSettingEnabled = true;
        request.MutationConfirmationState = "confirmed";
        request.AuthorizedLeaseContext = "sandbox";
        request.AuthorizedLeaseExpiresUtc = DateTime.UtcNow.AddMinutes(1);
        authorization.Audit(request, BridgeResult.Ok().WithMutation("mutation|detail\r\n"));

        string line = null;
        DateTime deadline = DateTime.UtcNow.AddSeconds(2);
        while (DateTime.UtcNow < deadline && line == null)
        {
            if (File.Exists(BridgePaths.AuditPath))
                line = File.ReadAllLines(BridgePaths.AuditPath).LastOrDefault(value => value.Contains(marker));
            if (line == null) Thread.Sleep(10);
        }
        Check(line != null, "audit projection was not written");
        Check(line.Contains("gameIdentity=game/identity  |saveIdentity=save-digest") &&
            line.Contains("mutation=mutation/detail  ") && line.Contains("leaseContext=sandbox") &&
            line.Contains("confirmation=confirmed"), "audit authorization projection changed");
        Check(!line.Contains("secret-transport-token-"), "audit projection leaked a transport token");
        Check(!line.Contains("AuthToken") && !line.Contains("lease="), "audit projection leaked secret fields");
    }

    private static void MutationConfirmationPromptStages()
    {
        BridgeMutationConfirmationSnapshot unconfirmed = new BridgeMutationConfirmationSnapshot(
            true, true, false, "missing", "session-prompt", "game-prompt", null, null);
        BridgeMutationConfirmationSnapshot confirmed = new BridgeMutationConfirmationSnapshot(
            true, true, true, "confirmed", "session-prompt", "game-prompt", null,
            DateTime.UtcNow);
        BridgeMutationConfirmationPrompt prompt = new BridgeMutationConfirmationPrompt();
        int confirmationCalls = 0;

        Equal(BridgeMutationConfirmation.Warning,
            "Remote tools may modify or destroy this game.", "confirmation warning text");
        Check(!prompt.ConfirmSecondStage(() => BridgeResult.Ok()),
            "confirmation callback ran before the second stage");
        Check(prompt.BeginSecondStage(unconfirmed) && prompt.IsAwaitingSecondConfirmation,
            "first confirmation stage did not open the second stage");
        prompt.CancelFirstStage();
        Check(!prompt.IsAwaitingSecondConfirmation && confirmationCalls == 0,
            "first-stage cancellation did not remain inert");
        Check(prompt.BeginSecondStage(unconfirmed), "second-stage dialog could not reopen");
        prompt.CancelSecondStage();
        Check(!prompt.IsAwaitingSecondConfirmation && confirmationCalls == 0,
            "second-stage cancellation invoked authority");
        Check(prompt.BeginSecondStage(unconfirmed), "second-stage dialog did not reopen");
        Check(prompt.ConfirmSecondStage(() => { confirmationCalls++; return BridgeResult.Ok(); }),
            "second-stage confirmation was not accepted");
        Check(confirmationCalls == 1 && !prompt.IsAwaitingSecondConfirmation,
            "confirmation authority was not invoked exactly once");
        Check(!prompt.BeginSecondStage(confirmed),
            "already confirmed state opened a second-stage dialog");
    }

    private static void MutationIdentityBoundaries()
    {
        Verse.Game game = (Verse.Game)FormatterServices.GetUninitializedObject(typeof(Verse.Game));
        string gameIdentity = BridgeMutationConfirmation.IdentityFor(game);
        Equal(null, BridgeMutationConfirmation.SaveIdentityFor(game),
            "new game unexpectedly received a save identity");

        object initData = FormatterServices.GetUninitializedObject(typeof(Verse.GameInitData));
        SetField(initData, "gameToLoad", "Save-A.rws");
        SetField(game, "initData", initData);
        string saveA = BridgeMutationConfirmation.SaveIdentityFor(game);
        Check(!string.IsNullOrWhiteSpace(saveA) && saveA != gameIdentity && !saveA.Contains("Save-A"),
            "loaded save identity was not independent and redacted");

        BridgeMutationConfirmation confirmation = new BridgeMutationConfirmation();
        confirmation.BindCurrentGame("session-identity", game);
        Check(confirmation.Confirm("session-identity", gameIdentity, saveA).Status == BridgeStatus.OK,
            "loaded save confirmation failed");
        SetField(initData, "gameToLoad", "Save-B.rws");
        string saveB = BridgeMutationConfirmation.SaveIdentityFor(game);
        Check(saveA != saveB && !confirmation.IsConfirmed("session-identity", gameIdentity, saveB),
            "changing the loaded save retained confirmation");
        confirmation.BindCurrentGame("session-identity", game);
        Check(confirmation.Confirm("session-identity", gameIdentity, saveB).Status == BridgeStatus.OK,
            "new loaded save could not be rebound and confirmed");
    }

    private static void WithTestGame(bool remoteMutationEnabled, Action action)
    {
        FieldInfo currentGameField = typeof(Verse.Current).GetField("gameInt",
            BindingFlags.Static | BindingFlags.NonPublic);
        object previousGame = currentGameField.GetValue(null);
        BridgeSettings previousSettings = RimWorldDevBridgeMod.Settings;
        try
        {
            RimWorldDevBridgeMod.Settings = new BridgeSettings
            {
                RemoteMutationEnabled = remoteMutationEnabled,
                ShowBridgeIndicator = false
            };
            Verse.Game game = (Verse.Game)FormatterServices.GetUninitializedObject(typeof(Verse.Game));
            currentGameField.SetValue(null, game);
            BridgeRuntime.BindCurrentGameForTests(game);
            BridgeRuntime.ApplyRemoteMutationSettings();
            action();
        }
        finally
        {
            BridgeRuntime.RevokeMutationConfirmation();
            currentGameField.SetValue(null, previousGame);
            RimWorldDevBridgeMod.Settings = previousSettings;
            BridgeRuntime.ApplyRemoteMutationSettings();
            InvokeRotateSession("mutation-test-game-cleanup");
        }
    }

    private static void ProductionPawnThingJobSnapshots()
    {
        InvokeRotateSession("production-query");
        FieldInfo currentGameField = typeof(Verse.Current).GetField("gameInt",
            BindingFlags.Static | BindingFlags.NonPublic);
        object previousGame = currentGameField.GetValue(null);
        TestMapFixture fixture = null;
        try
        {
            fixture = BuildTestMapFixture(1200);
            currentGameField.SetValue(null, fixture.Game);
            BridgeRuntime.OnRootUpdate();
            Check(fixture.Map.mapPawns.AllPawnsSpawned.Count == 1200,
                "pawn fixture count=" + fixture.Map.mapPawns.AllPawnsSpawned.Count);
            Check(fixture.Map.listerThings.AllThings.Count == 1200,
                "thing fixture count=" + fixture.Map.listerThings.AllThings.Count);
            Verse.Pawn removedPawn = fixture.Pawns[0];
            int mutationPawnFrames = RunProductionQuery(fixture.Map, "PAWNS",
                out BridgeResult mutationPawn, out BridgeRequest mutationPawnRequest,
                () => fixture.Pawns.RemoveAt(0));
            Check(mutationPawnFrames > 1 && mutationPawn.Status == BridgeStatus.PARTIAL &&
                mutationPawn.ContinuationCursor == null && mutationPawnRequest.CooperativeState == null,
                "pawn mutation during capture was not discarded");
            fixture.Pawns.Insert(0, removedPawn);

            Verse.Thing removedThing = fixture.Things[0];
            int mutationThingFrames = RunProductionQuery(fixture.Map, "THINGS",
                out BridgeResult mutationThing, out BridgeRequest mutationThingRequest,
                () => fixture.Things.RemoveAt(0));
            Check(mutationThingFrames > 1 && mutationThing.Status == BridgeStatus.PARTIAL &&
                mutationThing.ContinuationCursor == null && mutationThingRequest.CooperativeState == null,
                "thing mutation during capture was not discarded");
            fixture.Things.Insert(0, removedThing);

            Verse.Pawn removedJobPawn = fixture.Pawns[0];
            int mutationJobFrames = RunProductionQuery(fixture.Map, "JOBS",
                out BridgeResult mutationJob, out BridgeRequest mutationJobRequest,
                () => fixture.Pawns.RemoveAt(0));
            Check(mutationJobFrames > 1 && mutationJob.Status == BridgeStatus.PARTIAL &&
                mutationJob.ContinuationCursor == null && mutationJobRequest.CooperativeState == null,
                "job mutation during capture was not discarded");
            fixture.Pawns.Insert(0, removedJobPawn);

            BridgeDiagnostics.ResetProjectionMetricsForTests();
            int pawnFrames = RunProductionQuery(fixture.Map, "PAWNS", out BridgeResult pawnFirst,
                out BridgeRequest pawnRequest);
            int thingFrames = RunProductionQuery(fixture.Map, "THINGS", out BridgeResult thingFirst,
                out BridgeRequest thingRequest);
            int jobFrames = RunProductionQuery(fixture.Map, "JOBS", out BridgeResult jobFirst,
                out BridgeRequest jobRequest);
            Check(pawnFrames > 1 && thingFrames > 1 && jobFrames > 1,
                "production queries did not yield across frames pawn=" + pawnFrames +
                " thing=" + thingFrames + " job=" + jobFrames + " statuses=" +
                pawnFirst.Status + "/" + thingFirst.Status + "/" + jobFirst.Status + " errors=" +
                ResultDetails(pawnFirst) + "/" + ResultDetails(thingFirst) + "/" + ResultDetails(jobFirst));
            Check(pawnFrames < 200 && thingFrames < 200 && jobFrames < 200,
                "production query frame bounds were unbounded");
            int maxItems = BridgeDiagnostics.LastProjectionMaxItemsForTests;
            double maxStepMs = BridgeDiagnostics.LastProjectionMaxStepMsForTests;
            Console.WriteLine("productionSnapshotMetrics pawnFrames=" + pawnFrames +
                " thingFrames=" + thingFrames + " jobFrames=" + jobFrames +
                " mutationFrames=" + mutationPawnFrames + "/" + mutationThingFrames +
                "/" + mutationJobFrames + " maxItemsPerStep=" + maxItems +
                " maxStepMs=" + maxStepMs.ToString("F2", CultureInfo.InvariantCulture) +
                " budgetMs=" + BridgeRuntime.EffectiveMainThreadBudgetMs);
            Check(maxItems <= 32, "production snapshot step exceeded item bound: " + maxItems);
            Check(maxStepMs <= BridgeRuntime.EffectiveMainThreadBudgetMs + 20d,
                "production snapshot step exceeded timer tolerance: " + maxStepMs);
            Check(pawnFirst != null && thingFirst != null && jobFirst != null,
                "production query did not complete");
            Check(pawnFirst.ContinuationCursor != null && thingFirst.ContinuationCursor != null &&
                jobFirst.ContinuationCursor != null, "production query issued no stable cursor");

            fixture.Pawns.RemoveAt(0);
            fixture.Things.RemoveAt(0);
            BridgeResult pawnPage = RunCursorQuery(fixture.Map, "PAWNS", pawnFirst.ContinuationCursor);
            BridgeResult thingPage = RunCursorQuery(fixture.Map, "THINGS", thingFirst.ContinuationCursor);
            BridgeResult jobPage = RunCursorQuery(fixture.Map, "JOBS", jobFirst.ContinuationCursor);
            Check(pawnPage.Status == BridgeStatus.OK && thingPage.Status == BridgeStatus.OK &&
                jobPage.Status == BridgeStatus.OK, "immutable cursor page failed after live mutation");
            Check(pawnPage.Lines.Count > 0 && thingPage.Lines.Count > 0 && jobPage.Lines.Count > 0,
                "immutable cursor page omitted captured rows");
            Check(pawnPage.Lines[0].Contains(":26 "),
                "pawn snapshot ordering changed");
            Check(thingPage.Lines[0].Contains(":26 "),
                "thing snapshot ordering changed");
            Check(jobPage.Lines[0].Contains(":26 "),
                "job snapshot ordering changed");
            Check(pawnRequest.CooperativeState == null && thingRequest.CooperativeState == null &&
                jobRequest.CooperativeState == null, "partial production snapshot was retained");
        }
        finally
        {
            currentGameField.SetValue(null, previousGame);
            BridgeQuerySnapshotStore.RotateSession();
            InvokeRotateSession("production-query-cleanup");
        }
    }

    private static int RunProductionQuery(Verse.Map map, string command, out BridgeResult result,
        out BridgeRequest request, Action afterFirstYield = null)
    {
        BridgeRequest localRequest = Request("production-" + command, BridgeRuntime.SessionId);
        localRequest.Command = command;
        localRequest.Argument = "limit=25";
        BridgeExecutionContext context = new BridgeExecutionContext(localRequest, map, () => localRequest.Cancelled);
        int frames = 0;
        while (true)
        {
            frames++;
            result = BridgeDiagnostics.Execute(context);
            if (!localRequest.YieldExecution)
            {
                request = localRequest;
                return frames;
            }
            localRequest.YieldExecution = false;
            if (frames == 1) afterFirstYield?.Invoke();
            Check(frames < 200, command + " projection did not remain bounded");
        }
    }

    private static BridgeResult RunCursorQuery(Verse.Map map, string command, string cursor)
    {
        BridgeRequest request = Request("cursor-" + command, BridgeRuntime.SessionId);
        request.Command = command;
        request.Argument = "cursor=" + cursor;
        return BridgeDiagnostics.Execute(new BridgeExecutionContext(request, map, () => request.Cancelled));
    }

    private sealed class TestMapFixture
    {
        internal Verse.Game Game;
        internal Verse.Map Map;
        internal List<Verse.Pawn> Pawns;
        internal List<Verse.Thing> Things;
        internal int[] PawnIds;
        internal int[] ThingIds;
    }

    private sealed class HarnessPawn : Verse.Pawn
    {
        public override string LabelShortCap => "test pawn";
    }

    private sealed class HarnessThing : Verse.Thing
    {
        public override string LabelShortCap => "test thing";
    }

    private static TestMapFixture BuildTestMapFixture(int count)
    {
        Verse.Map map = new Verse.Map();
        SetField(map, "uniqueID", 7001);
        Verse.MapPawns mapPawns = new Verse.MapPawns(map);
        List<Verse.Pawn> pawns = new List<Verse.Pawn>();
        int[] pawnIds = new int[count];
        Verse.PawnKindDef kind = (Verse.PawnKindDef)FormatterServices.GetUninitializedObject(
            typeof(Verse.PawnKindDef));
        SetField(kind, "defName", "TestPawnKind");
        SetField(kind, "label", "test pawn");
        Verse.ThingDef pawnDef = (Verse.ThingDef)FormatterServices.GetUninitializedObject(
            typeof(Verse.ThingDef));
        SetField(pawnDef, "defName", "TestPawn");
        SetField(pawnDef, "label", "test pawn");
        Verse.JobDef jobDef = (Verse.JobDef)FormatterServices.GetUninitializedObject(
            typeof(Verse.JobDef));
        SetField(jobDef, "defName", "TestJob");
        SetField(jobDef, "label", "test job");
        for (int index = 0; index < count; index++)
        {
            int id = count - index;
            Verse.Pawn pawn = new HarnessPawn();
            SetField(pawn, "thingIDNumber", id);
            SetField(pawn, "mapIndexOrState", (sbyte)0);
            SetField(pawn, "def", pawnDef);
            SetField(pawn, "kindDef", kind);
            SetField(pawn, "health", FormatterServices.GetUninitializedObject(
                typeof(Verse.Pawn_HealthTracker)));
            Verse.AI.Pawn_JobTracker jobs = new Verse.AI.Pawn_JobTracker(pawn);
            Verse.AI.Job job = (Verse.AI.Job)FormatterServices.GetUninitializedObject(typeof(Verse.AI.Job));
            SetField(job, "def", jobDef);
            SetField(jobs, "curJob", job);
            SetField(pawn, "jobs", jobs);
            pawns.Add(pawn);
            pawnIds[index] = id;
        }
        SetField(mapPawns, "pawnsSpawned", pawns);
        SetField(mapPawns, "allPawnsResult", pawns);
        SetField(map, "mapPawns", mapPawns);

        Verse.ThingDef thingDef = (Verse.ThingDef)FormatterServices.GetUninitializedObject(
            typeof(Verse.ThingDef));
        SetField(thingDef, "defName", "TestThing");
        SetField(thingDef, "label", "test thing");
        List<Verse.Thing> things = new List<Verse.Thing>();
        int[] thingIds = new int[count];
        for (int index = 0; index < count; index++)
        {
            int id = count - index;
            Verse.Thing thing = new HarnessThing();
            SetField(thing, "thingIDNumber", id);
            SetField(thing, "mapIndexOrState", (sbyte)0);
            SetField(thing, "def", thingDef);
            SetField(thing, "stackCount", 1);
            things.Add(thing);
            thingIds[index] = id;
        }
        Verse.ListerThings lister = new Verse.ListerThings(Verse.ListerThingsUse.Global,
            new Verse.ThingListChangedCallbacks());
        var listsByDef = new Dictionary<Verse.ThingDef, List<Verse.Thing>>
        {
            [thingDef] = things
        };
        SetField(lister, "listsByDef", listsByDef);
        List<Verse.Thing>[] listsByGroup = new List<Verse.Thing>[128];
        for (int index = 0; index < listsByGroup.Length; index++) listsByGroup[index] = things;
        SetField(lister, "listsByGroup", listsByGroup);
        SetField(map, "listerThings", lister);

        Verse.Game game = (Verse.Game)FormatterServices.GetUninitializedObject(typeof(Verse.Game));
        SetField(game, "maps", new List<Verse.Map> { map });
        SetField(game, "currentMapIndex", (sbyte)0);
        return new TestMapFixture
        {
            Game = game,
            Map = map,
            Pawns = pawns,
            Things = things,
            PawnIds = pawnIds,
            ThingIds = thingIds
        };
    }

    private static void SetField(object target, string name, object value)
    {
        Type type = target.GetType();
        FieldInfo field = null;
        while (type != null && field == null)
        {
            field = type.GetField(name, BindingFlags.Instance | BindingFlags.Static |
                BindingFlags.NonPublic | BindingFlags.Public | BindingFlags.DeclaredOnly);
            type = type.BaseType;
        }
        Check(field != null, "missing fixture field " + target.GetType().Name + "." + name);
        field.SetValue(target, value);
    }

    private static void CooperativeSchedulerYielding()
    {
        BridgeMainThreadContext context = new BridgeMainThreadContext();
        int steps = 0;
        BridgeScheduler scheduler = new BridgeScheduler(request =>
        {
            steps++;
            if (steps <= 4)
            {
                Thread.Sleep(2);
                request.YieldExecution = true;
                return null;
            }
            return BridgeResult.Ok("test.cooperative");
        });
        scheduler.Configure(context, "s", 8, 1);
        BridgeRequest request = Request("cooperative", "s");
        Check(scheduler.Enqueue(request) == null, "cooperative enqueue");
        for (int frame = 0; frame < 4; frame++)
        {
            Equal(1, context.Drain(1, 100), "cooperative step was not frame bounded");
            Check(!request.Done.IsSet, "cooperative request completed early");
        }
        Equal(1, context.Drain(1, 100), "cooperative completion was not scheduled");
        Check(request.Done.IsSet, "cooperative request did not complete");
        Equal(5, steps, "cooperative step count");
        Equal(5, request.Result.CooperativeSteps, "cooperative diagnostics steps");
        Check(request.Result.MainThreadOverrun, "cooperative overrun was not retained");
        Check(request.Result.MaxMainThreadStepMs >= 2d, "cooperative maximum step was not recorded");
    }

    private static void CooperativeCancellation()
    {
        BridgeMainThreadContext context = new BridgeMainThreadContext();
        int steps = 0;
        BridgeScheduler scheduler = new BridgeScheduler(request =>
        {
            steps++;
            request.YieldExecution = true;
            return null;
        });
        scheduler.Configure(context, "s", 8, 12);
        BridgeRequest request = Request("cooperative-cancel", "s");
        Check(scheduler.Enqueue(request) == null, "cancel enqueue");
        context.Drain(1, 100);
        request.Cancelled = true;
        context.Drain(1, 100);
        Equal(1, steps, "cancelled cooperative request resumed");
        Equal(BridgeStatus.CANCELLED, request.Result.Status, "cooperative cancel status");
    }

    private static void CooperativeDeadline()
    {
        BridgeMainThreadContext context = new BridgeMainThreadContext();
        int steps = 0;
        BridgeScheduler scheduler = new BridgeScheduler(request =>
        {
            steps++;
            request.YieldExecution = true;
            return null;
        });
        scheduler.Configure(context, "s", 8, 12);
        BridgeRequest request = Request("cooperative-deadline", "s");
        Check(scheduler.Enqueue(request) == null, "deadline enqueue");
        context.Drain(1, 100);
        request.DeadlineUtc = DateTime.UtcNow.AddMilliseconds(-1);
        context.Drain(1, 100);
        Equal(1, steps, "expired cooperative request resumed");
        Equal(BridgeStatus.TIMEOUT, request.Result.Status, "cooperative deadline status");
    }

    private static void LargeBuiltInQueryYields()
    {
        BridgeMainThreadContext context = new BridgeMainThreadContext();
        int processed = 0;
        int frames = 0;
        BridgeScheduler scheduler = new BridgeScheduler(request =>
        {
            int before = processed;
            processed += 500;
            if (processed < 10000)
            {
                request.YieldExecution = true;
                return null;
            }
            return BridgeResult.Ok("core.pawns").Add("processed", processed).Add("lastFrame", processed - before);
        });
        scheduler.Configure(context, "s", 8, 12);
        BridgeRequest request = Request("large-pawns", "s");
        request.Command = "PAWNS";
        Check(scheduler.Enqueue(request) == null, "large query enqueue");
        while (!request.Done.IsSet && frames < 30)
        {
            frames++;
            context.Drain(1, 100);
        }
        Check(request.Done.IsSet, "large query did not complete");
        Check(frames > 1, "large query monopolized one frame");
        Equal(10000, processed, "large query work");
        Equal(500, int.Parse(FieldValue(request.Result, "lastFrame")), "large query frame chunk");
    }

    private static void SchedulerReconfiguration()
    {
        QueuedContext context = new QueuedContext();
        BridgeScheduler scheduler = new BridgeScheduler(_ => BridgeResult.Ok());
        scheduler.Configure(context, "s", 8, 3);
        BridgeRequest first = Request("reconfigure-first", "s");
        Check(scheduler.Enqueue(first) == null, "initial enqueue");
        scheduler.Reconfigure(16, 9);
        Equal(16, scheduler.QueueCapacity, "effective queue capacity");
        Equal(9, scheduler.MainThreadBudgetMs, "effective budget");
        for (int i = 0; i < 15; i++)
            Check(scheduler.Enqueue(Request("reconfigure-" + i, "s")) == null, "queued request lost " + i);
        Equal(16, int.Parse(FieldValue(scheduler.Metrics(), "queueDepth")), "queued depth after reconfigure");
        context.Drain();
        Equal(BridgeStatus.OK, first.Result.Status, "queued request result");

        QueuedContext runningContext = new QueuedContext();
        ManualResetEventSlim entered = new ManualResetEventSlim(false);
        ManualResetEventSlim release = new ManualResetEventSlim(false);
        BridgeScheduler running = new BridgeScheduler(_ =>
        {
            entered.Set();
            Check(release.Wait(5000), "in-flight request was not released");
            return BridgeResult.Ok();
        });
        running.Configure(runningContext, "s", 8, 3);
        BridgeRequest inFlight = Request("reconfigure-running", "s");
        Check(running.Enqueue(inFlight) == null, "in-flight enqueue");
        Thread drain = new Thread(runningContext.Drain);
        drain.Start();
        Check(entered.Wait(5000), "in-flight request did not start");
        running.Reconfigure(32, 12);
        release.Set();
        Check(drain.Join(5000), "in-flight drain did not finish");
        Equal(BridgeStatus.OK, inFlight.Result.Status, "in-flight request result");
        Equal(32, running.QueueCapacity, "in-flight capacity reconfigure");
        Equal(12, running.MainThreadBudgetMs, "in-flight budget reconfigure");
    }

    private static void SchedulerCompletionTiming()
    {
        QueuedContext context = new QueuedContext();
        double recordedMs = 0d;
        BridgeScheduler scheduler = new BridgeScheduler(_ =>
        {
            Thread.Sleep(15);
            return BridgeResult.Ok();
        }, (_, result) => recordedMs = result.ExecutionMs);
        scheduler.Configure(context, "s", 8, 3);
        BridgeRequest request = Request("timing", "s");
        Check(scheduler.Enqueue(request) == null, "enqueue");
        context.Drain();
        Check(recordedMs >= 10d, "completion observed zero execution time");
        Check(request.Result.Warnings.Any(value => value.Contains("slow main-thread")), "slow warning missing");
    }

    private static void InFlightCancellation()
    {
        QueuedContext context = new QueuedContext();
        ManualResetEventSlim entered = new ManualResetEventSlim(false);
        BridgeScheduler scheduler = new BridgeScheduler(request =>
        {
            entered.Set();
            while (!request.Cancelled) Thread.Sleep(1);
            throw new OperationCanceledException();
        });
        scheduler.Configure(context, "s", 8, 3);
        BridgeRequest request = Request("running", "s");
        Check(scheduler.Enqueue(request) == null, "enqueue");
        Thread drain = new Thread(context.Drain);
        drain.Start();
        Check(entered.Wait(5000), "request did not start");
        Check(scheduler.Cancel(request.RequestId), "running request was not cancelled");
        Check(drain.Join(5000), "cancelled request did not finish");
        Equal(BridgeStatus.CANCELLED, request.Result.Status, "running cancel status");
    }

    private static void WriteLeaseAndIdempotency()
    {
        BridgeAuthorization auth = new BridgeAuthorization();
        auth.RotateSession("s");
        BridgeResult lease = auth.Acquire("sandbox", true);
        string token = lease.Data.Single(item => item.Name == "lease").Value;
        BridgeRequest request = Request("w1", "s");
        request.Command = "SET_SPEED";
        request.Argument = "1";
        request.Mode = BridgeCommandMode.Reversible;
        request.IdempotencyKey = "same";
        BridgeCommandDescriptor descriptor = new BridgeCommandDescriptor { Mode = request.Mode };
        Check(auth.Authorize(request, descriptor, token, true) == null, "authorized");
        BridgeResult original = BridgeResult.Ok().Add("value", 1);
        auth.Remember(request, original);
        BridgeRequest retry = Request("w2", "s");
        retry.Command = request.Command;
        retry.Argument = request.Argument;
        retry.Mode = request.Mode;
        retry.IdempotencyKey = request.IdempotencyKey;
        Check(auth.TryGetCompleted(retry, out BridgeResult replay), "cache miss");
        Equal("w2", replay.RequestId, "retry id");
        Check(replay.Warnings.Any(value => value.Contains("not executed again")), "replay warning");
    }

    private static void IdempotencyConflict()
    {
        BridgeAuthorization auth = new BridgeAuthorization();
        auth.RotateSession("s");
        BridgeRequest original = Request("one", "s");
        original.Command = "SET_SPEED";
        original.Argument = "1";
        original.Mode = BridgeCommandMode.Reversible;
        original.IdempotencyKey = "key";
        auth.Remember(original, BridgeResult.Ok());
        BridgeRequest conflict = Request("two", "s");
        conflict.Command = original.Command;
        conflict.Argument = "2";
        conflict.Mode = original.Mode;
        conflict.IdempotencyKey = original.IdempotencyKey;
        Check(auth.TryGetCompleted(conflict, out BridgeResult result), "conflict cache miss");
        Equal(BridgeStatus.INVALID_ARGUMENT, result.Status, "conflict status");
    }

    private static void PreExecutionFailureNotCached()
    {
        FieldInfo field = typeof(BridgeRuntime).GetField("Authorization",
            BindingFlags.Static | BindingFlags.NonPublic);
        BridgeAuthorization auth = (BridgeAuthorization)field.GetValue(null);
        auth.RotateSession("poison");
        BridgeRequest request = Request("denied", "poison");
        request.Command = "SET_SPEED";
        request.Argument = "1";
        request.Mode = BridgeCommandMode.Reversible;
        request.IdempotencyKey = "poison-key";
        request.PreparedDescriptor = new BridgeCommandDescriptor
        {
            Name = request.Command, Provider = "core", ProviderVersion = BridgeProtocol.BridgeVersion,
            Mode = request.Mode, Cost = BridgeCostClass.Trivial
        };
        FieldInfo executorField = typeof(BridgeRuntime).GetField("RequestExecutor",
            BindingFlags.Static | BindingFlags.NonPublic);
        object executor = executorField.GetValue(null);
        MethodInfo complete = executor.GetType().GetMethod("Complete",
            BindingFlags.Instance | BindingFlags.NonPublic);
        complete.Invoke(executor, new object[] { request,
            BridgeResult.Fail(BridgeStatus.FORBIDDEN, "write_lease_required") });
        Check(!auth.TryGetCompleted(request, out _), "pre-execution failure poisoned idempotency cache");
    }

    private static void IdempotencyCopyPreservesBounds()
    {
        BridgeResult source = BridgeResult.Ok();
        for (int i = 0; i < 513; i++) source.Data.Add(new BridgeField("field" + i, "value"));
        source.Data[0].Value = new string('x', 20000);

        BridgeResult copy = source.CopyFor("replay");

        Check(source.Truncated, "source truncation was not recorded before caching");
        Check(copy.Truncated, "cached copy lost truncation evidence");
        Equal(512, copy.Data.Count, "cached field bound");
        Check(copy.Data[0].Value.Length <= 16384, "cached field value was not bounded");
    }

    private static void ManifestAdapterLifecycle()
    {
        string root = Path.Combine(Path.GetTempPath(), "RimWorldDevBridgeHarness-" + Guid.NewGuid().ToString("N"));
        string adapters = Path.Combine(root, "DevTools", "HotAdapters");
        Directory.CreateDirectory(adapters);
        try
        {
            string built = Path.Combine(AppDomain.CurrentDomain.BaseDirectory, "BridgeFixtureAdapter.dll");
            if (!File.Exists(built))
                built = Path.GetFullPath(Path.Combine(AppDomain.CurrentDomain.BaseDirectory,
                    "..", "..", "..", "FixtureAdapter", "bin", "Release", "net472", "BridgeFixtureAdapter.dll"));
            Check(File.Exists(built), "fixture adapter was not built: " + built);
            byte[] bytes = File.ReadAllBytes(built);
            string identity = AssemblyName.GetAssemblyName(built).FullName;
            string hash = Sha256(bytes);
            WriteGeneration(adapters, bytes, identity, hash, "old", "2026-01-01T00:00:00Z", "FIXTURE_OLD", 1, 10);
            WriteGeneration(adapters, bytes, identity, hash, "new", "2026-02-01T00:00:00Z", "FIXTURE_ECHO", 1, 10);
            File.WriteAllBytes(Path.Combine(adapters, "unmanifested-history.dll"), bytes);
            File.WriteAllText(Path.Combine(adapters, "partial.manifest.json.tmp"), "{");
            File.WriteAllText(Path.Combine(adapters, "malformed.manifest.json"), "{");
            WriteGeneration(adapters, bytes, identity, hash, "collision", "2026-03-01T00:00:00Z", "STATUS", 1, 10,
                "collision-adapter");
            WriteGeneration(adapters, bytes, identity, hash, "incompatible", "2026-04-01T00:00:00Z", "FIXTURE_BAD", 99,
                100, "incompatible-adapter");

            BridgePaths.Initialize(root);
            BridgeAdapterCatalog.IndexSynchronouslyForTests(Array.Empty<string>());
            Check(!AppDomain.CurrentDomain.GetAssemblies().Any(item => item.GetName().Name == "BridgeFixtureAdapter"),
                "adapter loaded during indexing");
            Check(BridgeAdapterCatalog.Describe("FIXTURE_ECHO") != null, "new command missing");
            Check(BridgeAdapterCatalog.Describe("FIXTURE_OLD") == null, "old generation active");
            Check(BridgeAdapterCatalog.Describe("STATUS") == null, "colliding adapter command active");
            Equal("core", BridgeCommands.Describe("STATUS").Provider, "core collision displaced");
            BridgeRequest request = Request("fixture", "s");
            request.Command = "FIXTURE_ECHO";
            request.Argument = "hello";
            Check(BridgeAdapterCatalog.Prepare(request) == null, "prepare failed");
            Check(!AppDomain.CurrentDomain.GetAssemblies().Any(item => item.GetName().Name == "BridgeFixtureAdapter"),
                "adapter loaded during preparation");
            BridgeResult executed = BridgeAdapterCatalog.Execute(new BridgeExecutionContext(request, null, () => false));
            Check(AppDomain.CurrentDomain.GetAssemblies().Any(item => item.GetName().Name == "BridgeFixtureAdapter"),
                "adapter did not load on execution");
            Equal(BridgeStatus.OK, executed.Status, "execute status");
            Equal("hello", executed.Data.Single(item => item.Name == "value").Value, "echo");
            BridgeAdapterCatalog.IndexSynchronouslyForTests(Array.Empty<string>());
            Check(BridgeAdapterCatalog.Describe("FIXTURE_ECHO") != null, "loaded provider lost after reindex");
            BridgeResult health = BridgeAdapterCatalog.Health();
            Check(health.Data.Any(item => item.Name == "retainedGenerations" && item.Value == "1"), "retained count");
            Check(health.Warnings.Count > 0, "malformed manifest not reported");

        }
        finally
        {
            try { Directory.Delete(root, true); } catch { }
        }
    }

    private static void AdapterCatalogConcurrentReaders()
    {
        string root = Path.Combine(Path.GetTempPath(), "RimWorldDevBridgeCatalogRace-" + Guid.NewGuid().ToString("N"));
        string adapters = Path.Combine(root, "DevTools", "HotAdapters");
        Directory.CreateDirectory(adapters);
        try
        {
            string built = Path.Combine(AppDomain.CurrentDomain.BaseDirectory, "BridgeFixtureAdapter.dll");
            if (!File.Exists(built))
                built = Path.GetFullPath(Path.Combine(AppDomain.CurrentDomain.BaseDirectory,
                    "..", "..", "..", "FixtureAdapter", "bin", "Release", "net472", "BridgeFixtureAdapter.dll"));
            byte[] bytes = File.ReadAllBytes(built);
            string command = "CATALOG_RACE_" + Guid.NewGuid().ToString("N").Substring(0, 8);
            string adapterId = "catalog-race-" + Guid.NewGuid().ToString("N");
            WriteGeneration(adapters, bytes, AssemblyName.GetAssemblyName(built).FullName, Sha256(bytes),
                "1", "2026-08-01T00:00:00Z", command, 1, 10, adapterId);
            BridgePaths.Initialize(root);
            BridgeAdapterSourceRecord source = new BridgeAdapterSourceRecord(
                BridgeAdapterSourceKind.LegacyDevelopment, "Lan.RimWorldDevBridge", "legacy", adapters,
                "legacy:catalog-race", 1, Array.Empty<BridgeLoadedModuleRecord>());
            BridgeAdapterCatalog.IndexSynchronouslyForTests(Array.Empty<string>(), new[] { source });

            List<Exception> failures = new List<Exception>();
            object failureGate = new object();
            Thread[] readers = Enumerable.Range(0, 8).Select(_ => new Thread(() =>
            {
                try
                {
                    for (int i = 0; i < 100; i++)
                    {
                        string state = BridgeAdapterCatalog.State;
                        string fingerprint = BridgeAdapterCatalog.Fingerprint;
                        List<BridgeCommandDescriptor> commands = BridgeAdapterCatalog.Commands.ToList();
                        BridgeCommandDescriptor descriptor = BridgeAdapterCatalog.Describe(command);
                        bool available = BridgeAdapterCatalog.IsAvailable(adapterId);
                        BridgeResult health = BridgeAdapterCatalog.Health();
                    }
                }
                catch (Exception exception)
                {
                    lock (failureGate) failures.Add(exception);
                }
            })).ToArray();
            foreach (Thread reader in readers) reader.Start();
            for (int i = 0; i < 5; i++)
                BridgeAdapterCatalog.IndexAsynchronouslyForTests(Array.Empty<string>(), new[] { source }, i == 0 ? 150 : 0);
            foreach (Thread reader in readers) reader.Join();
            DateTime waitUntil = DateTime.UtcNow.AddSeconds(5);
            while (BridgeAdapterCatalog.Indexing && DateTime.UtcNow < waitUntil) Thread.Sleep(10);
            Check(failures.Count == 0, "concurrent catalog reader failed: " +
                (failures.Count == 0 ? string.Empty : failures[0].GetBaseException().Message));
            Check(!BridgeAdapterCatalog.Indexing, "concurrent catalog index did not settle");
            Check(BridgeAdapterCatalog.Describe(command) != null, "concurrent catalog lost active command");
        }
        finally
        {
            try { RestoreFixtureAdapterCatalog(); } catch { }
            try { Directory.Delete(root, true); } catch { }
        }
    }

    private static void OwnerAdapterDiscoveryAndDuplicateResolution()
    {
        string root = Path.Combine(Path.GetTempPath(), "RimWorldDevBridgeOwner-" + Guid.NewGuid().ToString("N"));
        string ownerRoot = Path.Combine(root, "OwnerMod");
        string secondOwnerRoot = Path.Combine(root, "SecondOwner");
        string legacyRoot = Path.Combine(root, "DevTools", "HotAdapters");
        string ownerDirectory = Path.Combine(ownerRoot, "DevTools", "BridgeAdapters");
        string secondDirectory = Path.Combine(secondOwnerRoot, "DevTools", "BridgeAdapters");
        Directory.CreateDirectory(ownerDirectory);
        Directory.CreateDirectory(secondDirectory);
        Directory.CreateDirectory(legacyRoot);
        try
        {
            string built = Path.Combine(AppDomain.CurrentDomain.BaseDirectory, "BridgeFixtureAdapter.dll");
            if (!File.Exists(built))
                built = Path.GetFullPath(Path.Combine(AppDomain.CurrentDomain.BaseDirectory,
                    "..", "..", "..", "FixtureAdapter", "bin", "Release", "net472", "BridgeFixtureAdapter.dll"));
            Check(File.Exists(built), "fixture adapter was not built for owner discovery");
            byte[] bytes = File.ReadAllBytes(built);
            string identity = AssemblyName.GetAssemblyName(built).FullName;
            string hash = Sha256(bytes);
            string ownerId = "owner.mod." + Guid.NewGuid().ToString("N");
            string secondId = "second.owner." + Guid.NewGuid().ToString("N");
            string ownerAdapter = "OWNER_DISCOVERY_" + Guid.NewGuid().ToString("N").Substring(0, 8);

            WriteGeneration(ownerDirectory, bytes, identity, hash, "1", "2026-07-01T00:00:00Z", ownerAdapter,
                1, 10, adapterId: ownerAdapter.ToLowerInvariant(), requiredPackageId: ownerId);
            WriteGeneration(legacyRoot, bytes, identity, hash, "1", "2026-07-01T00:00:00Z", ownerAdapter,
                1, 10, adapterId: ownerAdapter.ToLowerInvariant(), requiredPackageId: ownerId);

            BridgeAdapterSourceRecord ownerSource = new BridgeAdapterSourceRecord(
                BridgeAdapterSourceKind.OwnerMod, ownerId, "1.0", ownerRoot, "owner:" + ownerId,
                1, Array.Empty<BridgeLoadedModuleRecord>());
            BridgeAdapterSourceRecord legacySource = new BridgeAdapterSourceRecord(
                BridgeAdapterSourceKind.LegacyDevelopment, "Lan.RimWorldDevBridge", "legacy", legacyRoot,
                "legacy:DevTools/HotAdapters", 1, Array.Empty<BridgeLoadedModuleRecord>());
            BridgeAdapterCatalog.IndexSynchronouslyForTests(new[] { ownerId },
                new[] { legacySource, ownerSource });
            Check(BridgeAdapterCatalog.Commands.Any(item =>
                string.Equals(item.Name, ownerAdapter, StringComparison.OrdinalIgnoreCase)),
                "owner file adapter was not selected");
            BridgeResult health = BridgeAdapterCatalog.Health();
            Check(health.Lines.Any(line => line.Contains("sourceKind:OwnerMod") && line.Contains("sourcePackage:" + ownerId)),
                "owner source provenance was not reported");
            Check(health.Lines.Any(line => line.Contains("migration-duplicate") && line.Contains("owner copy preferred")),
                "identical owner/legacy migration duplicate was not reported");
            BridgeAdapterCatalog.IndexAsynchronouslyForTests(new[] { ownerId }, new[] { ownerSource }, 0);
            DateTime ownerWaitUntil = DateTime.UtcNow.AddSeconds(5);
            while (BridgeAdapterCatalog.Indexing && DateTime.UtcNow < ownerWaitUntil) Thread.Sleep(10);
            Check(BridgeAdapterCatalog.LastIndexThreadIdForTests != Thread.CurrentThread.ManagedThreadId,
                "adapter indexing unexpectedly ran on the harness thread");

            string loadedAdapter = "LOADED_DISCOVERY_" + Guid.NewGuid().ToString("N").Substring(0, 8);
            string loadedModulePath = Path.Combine(ownerRoot, "Framework.dll");
            File.Copy(built, loadedModulePath, true);
            Guid moduleMvid = Assembly.LoadFrom(built).ManifestModule.ModuleVersionId;
            WriteGeneration(ownerDirectory, bytes, identity, hash, "1", "2026-07-01T00:00:01Z", loadedAdapter,
                1, 10, adapterId: loadedAdapter.ToLowerInvariant(), requiredPackageId: ownerId);
            string loadedManifestPath = Path.Combine(ownerDirectory, loadedAdapter.ToLowerInvariant() + ".1.manifest.json");
            AdapterManifest loadedManifest = ReadManifest(loadedManifestPath);
            string generatedFile = Path.Combine(ownerDirectory, loadedAdapter.ToLowerInvariant() + ".1.dll");
            if (File.Exists(generatedFile)) File.Delete(generatedFile);
            loadedManifest.assemblySource = "loaded";
            loadedManifest.assemblyFile = null;
            loadedManifest.modulePackageId = ownerId;
            loadedManifest.moduleRelativePath = "Framework.dll";
            loadedManifest.moduleMvid = moduleMvid.ToString("D");
            WriteManifest(loadedManifestPath, loadedManifest);
            BridgeLoadedModuleRecord loadedModule = new BridgeLoadedModuleRecord(ownerId, "Framework.dll",
                loadedModulePath, identity, moduleMvid, bytes.Length);
            BridgeAdapterSourceRecord loadedSource = new BridgeAdapterSourceRecord(
                BridgeAdapterSourceKind.OwnerMod, ownerId, "1.0", ownerRoot, "owner:" + ownerId,
                2, new[] { loadedModule });
            BridgeAdapterCatalog.IndexSynchronouslyForTests(new[] { ownerId }, new[] { loadedSource });
            Check(BridgeAdapterCatalog.Commands.Any(item =>
                string.Equals(item.Name, loadedAdapter, StringComparison.OrdinalIgnoreCase)),
                "owner loaded-assembly adapter was not selected");

            string missingOwner = "missing.owner." + Guid.NewGuid().ToString("N");
            string missingRoot = Path.Combine(root, "MissingOwner");
            string missingDirectory = Path.Combine(missingRoot, "DevTools", "BridgeAdapters");
            Directory.CreateDirectory(missingDirectory);
            string missingAdapter = "MISSING_OWNER_" + Guid.NewGuid().ToString("N").Substring(0, 8);
            WriteGeneration(missingDirectory, bytes, identity, hash, "1", "2026-07-01T00:00:00Z", missingAdapter,
                1, 10, adapterId: missingAdapter.ToLowerInvariant(), requiredPackageId: missingOwner);
            BridgeAdapterSourceRecord missingSource = new BridgeAdapterSourceRecord(
                BridgeAdapterSourceKind.OwnerMod, missingOwner, "1.0", missingRoot, "owner:" + missingOwner,
                2, Array.Empty<BridgeLoadedModuleRecord>());
            BridgeAdapterCatalog.IndexSynchronouslyForTests(Array.Empty<string>(), new[] { missingSource });
            Check(!BridgeAdapterCatalog.Commands.Any(item =>
                string.Equals(item.Name, missingAdapter, StringComparison.OrdinalIgnoreCase)),
                "unloaded owner adapter was accepted");
            BridgeResult missingHealth = BridgeAdapterCatalog.Health();
            Check(missingHealth.Lines.Any(value => value.Contains("missing package")) ||
                missingHealth.Warnings.Any(value => value.Contains("missing package")),
                "missing owner package was not diagnosed: " + string.Join(";", missingHealth.Lines));

            string unsafeAdapter = "UNSAFE_OWNER_" + Guid.NewGuid().ToString("N").Substring(0, 8);
            WriteGeneration(ownerDirectory, bytes, identity, hash, "1", "2026-07-02T00:00:00Z", unsafeAdapter,
                1, 10, adapterId: unsafeAdapter.ToLowerInvariant(), requiredPackageId: ownerId);
            string unsafeManifest = Path.Combine(ownerDirectory, unsafeAdapter.ToLowerInvariant() + ".1.manifest.json");
            AdapterManifest unsafeValue = ReadManifest(unsafeManifest);
            unsafeValue.assemblyFile = "../outside.dll";
            WriteManifest(unsafeManifest, unsafeValue);
            BridgeAdapterCatalog.IndexSynchronouslyForTests(new[] { ownerId }, new[] { ownerSource });
            Check(!BridgeAdapterCatalog.Commands.Any(item =>
                string.Equals(item.Name, unsafeAdapter, StringComparison.OrdinalIgnoreCase)),
                "traversal adapter was accepted");
            Check(BridgeAdapterCatalog.Health().Warnings.Any(value => value.Contains(unsafeAdapter.ToLowerInvariant())),
                "traversal adapter was not diagnosed");

            string conflictAdapter = "CONFLICT_OWNER_" + Guid.NewGuid().ToString("N").Substring(0, 8);
            WriteGeneration(ownerDirectory, bytes, identity, hash, "1", "2026-07-03T00:00:00Z", conflictAdapter,
                1, 10, adapterId: conflictAdapter.ToLowerInvariant(), providerType: "Owner.Provider",
                requiredPackageId: ownerId);
            WriteGeneration(secondDirectory, bytes, identity, hash, "1", "2026-07-03T00:00:00Z", conflictAdapter,
                1, 10, adapterId: conflictAdapter.ToLowerInvariant(), providerType: "Second.Provider",
                requiredPackageId: secondId);
            BridgeAdapterSourceRecord secondSource = new BridgeAdapterSourceRecord(
                BridgeAdapterSourceKind.OwnerMod, secondId, "1.0", secondOwnerRoot, "owner:" + secondId,
                3, Array.Empty<BridgeLoadedModuleRecord>());
            BridgeAdapterCatalog.IndexSynchronouslyForTests(new[] { ownerId, secondId },
                new[] { ownerSource, secondSource });
            Check(!BridgeAdapterCatalog.Commands.Any(item =>
                string.Equals(item.Name, conflictAdapter, StringComparison.OrdinalIgnoreCase)),
                "conflicting generation was selected");
            Check(BridgeAdapterCatalog.Health().Warnings.Any(value => value.Contains("conflicting immutable bindings")),
                "conflicting generation was not diagnosed");

            string collisionAdapterA = "COLLISION_A_" + Guid.NewGuid().ToString("N").Substring(0, 8);
            string collisionAdapterB = "COLLISION_B_" + Guid.NewGuid().ToString("N").Substring(0, 8);
            WriteGeneration(ownerDirectory, bytes, identity, hash, "1", "2026-07-04T00:00:00Z", "OWNER_COLLISION",
                1, 10, adapterId: collisionAdapterA.ToLowerInvariant(), requiredPackageId: ownerId);
            WriteGeneration(secondDirectory, bytes, identity, hash, "1", "2026-07-04T00:00:00Z", "OWNER_COLLISION",
                1, 10, adapterId: collisionAdapterB.ToLowerInvariant(), requiredPackageId: secondId);
            BridgeAdapterCatalog.IndexSynchronouslyForTests(new[] { ownerId, secondId },
                new[] { ownerSource, secondSource });
            Check(BridgeAdapterCatalog.Health().Warnings.Any(value => value.Contains("command collision OWNER_COLLISION")),
                "owner command collision was not diagnosed");

            string raceOldRoot = Path.Combine(root, "RaceOldOwner");
            string raceNewRoot = Path.Combine(root, "RaceNewOwner");
            string raceOldDirectory = Path.Combine(raceOldRoot, "DevTools", "BridgeAdapters");
            string raceNewDirectory = Path.Combine(raceNewRoot, "DevTools", "BridgeAdapters");
            Directory.CreateDirectory(raceOldDirectory);
            Directory.CreateDirectory(raceNewDirectory);
            string oldCommand = "RACE_OLD_" + Guid.NewGuid().ToString("N").Substring(0, 8);
            string newCommand = "RACE_NEW_" + Guid.NewGuid().ToString("N").Substring(0, 8);
            string raceOldId = "race.old." + Guid.NewGuid().ToString("N");
            string raceNewId = "race.new." + Guid.NewGuid().ToString("N");
            WriteGeneration(raceOldDirectory, bytes, identity, hash, "1", "2026-07-05T00:00:00Z", oldCommand,
                1, 10, adapterId: "race-old-" + raceOldId, requiredPackageId: raceOldId);
            WriteGeneration(raceNewDirectory, bytes, identity, hash, "2", "2026-07-06T00:00:00Z", newCommand,
                1, 10, adapterId: "race-new-" + raceNewId, requiredPackageId: raceNewId);
            BridgeAdapterSourceRecord oldRaceSource = new BridgeAdapterSourceRecord(
                BridgeAdapterSourceKind.OwnerMod, raceOldId, "1.0", raceOldRoot, "owner:" + raceOldId,
                4, Array.Empty<BridgeLoadedModuleRecord>());
            BridgeAdapterSourceRecord newRaceSource = new BridgeAdapterSourceRecord(
                BridgeAdapterSourceKind.OwnerMod, raceNewId, "1.0", raceNewRoot, "owner:" + raceNewId,
                5, Array.Empty<BridgeLoadedModuleRecord>());
            BridgeAdapterCatalog.IndexAsynchronouslyForTests(new[] { raceOldId }, new[] { oldRaceSource }, 250);
            BridgeAdapterCatalog.IndexAsynchronouslyForTests(new[] { raceNewId }, new[] { newRaceSource }, 0);
            DateTime waitUntil = DateTime.UtcNow.AddSeconds(5);
            while (BridgeAdapterCatalog.Indexing && DateTime.UtcNow < waitUntil) Thread.Sleep(10);
            Check(BridgeAdapterCatalog.Commands.Any(item =>
                string.Equals(item.Name, newCommand, StringComparison.OrdinalIgnoreCase)),
                "new adapter index did not commit");
            Check(!BridgeAdapterCatalog.Commands.Any(item =>
                string.Equals(item.Name, oldCommand, StringComparison.OrdinalIgnoreCase)),
                "stale adapter index replaced the newer result");
        }
        finally
        {
            try { RestoreFixtureAdapterCatalog(); } catch { }
            try { Directory.Delete(root, true); } catch { }
        }
    }

    private static void OwnerPackagedAdapterIntegration(string encodedOwners)
    {
        List<BridgeAdapterSourceRecord> sources = new List<BridgeAdapterSourceRecord>();
        List<string> packages = new List<string>();
        string[] entries = encodedOwners.Split(new[] { '\n' }, StringSplitOptions.RemoveEmptyEntries);
        long sourceGeneration = 1;
        foreach (string entry in entries)
        {
            string[] parts = entry.Split('|');
            Check(parts.Length == 3, "owner integration entry must contain package, root, and adapter ID: " + entry);
            string packageId = parts[0];
            string root = Path.GetFullPath(parts[1]);
            string expectedAdapterId = parts[2];
            string directory = Path.Combine(root, "DevTools", "BridgeAdapters");
            string[] manifests = Directory.Exists(directory)
                ? Directory.GetFiles(directory, "*.manifest.json", SearchOption.TopDirectoryOnly)
                : Array.Empty<string>();
            Check(manifests.Length == 1, packageId + " must publish exactly one current manifest");
            AdapterManifest manifest = ReadManifest(manifests[0]);
            Equal(expectedAdapterId, manifest.adapterId, packageId + " adapter ID");
            Check(manifest.requiredPackageIds != null && manifest.requiredPackageIds.Contains(packageId),
                packageId + " owner package declaration");
            packages.Add(packageId);
            List<BridgeLoadedModuleRecord> modules = new List<BridgeLoadedModuleRecord>();
            if (string.Equals(manifest.assemblySource, "loaded", StringComparison.OrdinalIgnoreCase))
            {
                Equal(packageId, manifest.modulePackageId, packageId + " loaded module package");
                string relative = (manifest.moduleRelativePath ?? string.Empty).Replace('/', Path.DirectorySeparatorChar);
                string modulePath = Path.GetFullPath(Path.Combine(root, relative));
                Check(File.Exists(modulePath), packageId + " loaded module exists");
                AssemblyName assemblyName = AssemblyName.GetAssemblyName(modulePath);
                Guid mvid = Assembly.ReflectionOnlyLoadFrom(modulePath).ManifestModule.ModuleVersionId;
                string hash = Sha256(File.ReadAllBytes(modulePath));
                Equal(assemblyName.FullName, manifest.assemblyIdentity, packageId + " loaded module identity");
                Equal(mvid.ToString("D"), manifest.moduleMvid, packageId + " loaded module MVID");
                Equal(hash, manifest.contentHash, packageId + " loaded module hash");
                modules.Add(new BridgeLoadedModuleRecord(packageId, manifest.moduleRelativePath, modulePath,
                    assemblyName.FullName, mvid, new FileInfo(modulePath).Length));
            }
            sources.Add(new BridgeAdapterSourceRecord(BridgeAdapterSourceKind.OwnerMod, packageId,
                manifest.version, root, "owner:" + packageId, sourceGeneration++, modules));
        }

        try
        {
            BridgeAdapterCatalog.IndexSynchronouslyForTests(packages, sources);
            foreach (BridgeAdapterSourceRecord source in sources)
            {
                string manifestPath = Directory.GetFiles(source.DirectoryPath, "*.manifest.json",
                    SearchOption.TopDirectoryOnly).Single();
                AdapterManifest manifest = ReadManifest(manifestPath);
                Check(BridgeAdapterCatalog.Health().Lines.Any(line =>
                    line.Contains("sourcePackage:" + source.PackageId) &&
                    line.Contains("sourceKind:OwnerMod")),
                    source.PackageId + " provenance health");
                IEnumerable<AdapterCommandManifest> commands = manifest.commands ??
                    new List<AdapterCommandManifest>();
                foreach (AdapterCommandManifest command in commands)
                    Check(BridgeAdapterCatalog.Commands.Any(item =>
                        string.Equals(item.Name, command.name, StringComparison.OrdinalIgnoreCase)),
                        source.PackageId + " command " + command.name);
            }

            BridgeAdapterSourceRecord disabledSource = sources[0];
            string disabledManifestPath = Directory.GetFiles(disabledSource.DirectoryPath,
                "*.manifest.json", SearchOption.TopDirectoryOnly).Single();
            AdapterManifest disabledManifest = ReadManifest(disabledManifestPath);
            string disabledCommand = (disabledManifest.commands ?? new List<AdapterCommandManifest>())
                .Select(command => command.name).FirstOrDefault();
            List<BridgeAdapterSourceRecord> enabledAfterDisable = sources
                .Where(source => !string.Equals(source.PackageId, disabledSource.PackageId,
                    StringComparison.OrdinalIgnoreCase)).ToList();
            List<string> packagesAfterDisable = packages
                .Where(package => !string.Equals(package, disabledSource.PackageId,
                    StringComparison.OrdinalIgnoreCase)).ToList();
            BridgeAdapterCatalog.IndexSynchronouslyForTests(packagesAfterDisable, enabledAfterDisable);
            Check(!BridgeAdapterCatalog.Commands.Any(item =>
                string.Equals(item.Name, disabledCommand, StringComparison.OrdinalIgnoreCase)),
                disabledSource.PackageId + " disabled owner remained indexed");
        }
        finally
        {
            RestoreFixtureAdapterCatalog();
        }
    }

    private static void SlowLegacyAdapterCircuitBreaker()
    {
        string root = Path.Combine(Path.GetTempPath(), "RimWorldDevBridgeLegacy-" + Guid.NewGuid().ToString("N"));
        string adapters = Path.Combine(root, "DevTools", "HotAdapters");
        Directory.CreateDirectory(adapters);
        try
        {
            string built = Path.Combine(AppDomain.CurrentDomain.BaseDirectory, "BridgeFixtureAdapter.dll");
            if (!File.Exists(built))
                built = Path.GetFullPath(Path.Combine(AppDomain.CurrentDomain.BaseDirectory,
                    "..", "..", "..", "FixtureAdapter", "bin", "Release", "net472", "BridgeFixtureAdapter.dll"));
            byte[] bytes = File.ReadAllBytes(built);
            string identity = AssemblyName.GetAssemblyName(built).FullName;
            WriteGeneration(adapters, bytes, identity, Sha256(bytes), "legacy-slow", "2026-06-01T00:00:00Z",
                "LEGACY_SLOW", 1, 10, adapterId: "legacy-slow",
                providerType: "BridgeFixtureAdapter.LegacySlowProvider");
            BridgePaths.Initialize(root);
            BridgeAdapterCatalog.IndexSynchronouslyForTests(Array.Empty<string>());
            BridgeRequest request = Request("legacy-slow", "s");
            request.Command = "LEGACY_SLOW";
            Check(BridgeAdapterCatalog.Prepare(request) == null, "legacy slow prepare");
            BridgeResult first = BridgeAdapterCatalog.Execute(new BridgeExecutionContext(request, null, () => false));
            Check(first.NonCooperativeExecution, "legacy execution was not marked non-cooperative");
            Check(first.Warnings.Any(value => value.Contains("non-cooperative")), "legacy contract warning missing");
            Check(first.ExecutionMs >= 250d, "legacy elapsed time was not measured");

            BridgeRequest second = Request("legacy-slow-2", "s");
            second.Command = "LEGACY_SLOW";
            Equal(BridgeStatus.UNAVAILABLE,
                BridgeAdapterCatalog.Execute(new BridgeExecutionContext(second, null, () => false)).Status,
                "serious legacy overrun did not open the circuit");
        }
        finally
        {
            BridgePaths.Initialize(HarnessUserRoot);
            try { Directory.Delete(root, true); } catch { }
        }
    }

    private static void ManifestGenerationReuseRejected()
    {
        string root = Path.Combine(Path.GetTempPath(), "RimWorldDevBridgeReuse-" + Guid.NewGuid().ToString("N"));
        string adapters = Path.Combine(root, "DevTools", "HotAdapters");
        Directory.CreateDirectory(adapters);
        try
        {
            string built = Path.Combine(AppDomain.CurrentDomain.BaseDirectory, "BridgeFixtureAdapter.dll");
            if (!File.Exists(built))
                built = Path.GetFullPath(Path.Combine(AppDomain.CurrentDomain.BaseDirectory,
                    "..", "..", "..", "FixtureAdapter", "bin", "Release", "net472", "BridgeFixtureAdapter.dll"));
            byte[] bytes = File.ReadAllBytes(built);
            string identity = AssemblyName.GetAssemblyName(built).FullName;
            string hash = Sha256(bytes);
            WriteGeneration(adapters, bytes, identity, hash, "same", "2026-05-01T00:00:00Z", "FIXTURE_ECHO", 1, 10);
            BridgePaths.Initialize(root);
            BridgeAdapterCatalog.IndexSynchronouslyForTests(Array.Empty<string>());
            BridgeRequest initial = Request("fixture-initial", "s");
            initial.Command = "FIXTURE_ECHO";
            Check(BridgeAdapterCatalog.Prepare(initial) == null, "initial adapter prepare failed");
            Equal(BridgeStatus.OK,
                BridgeAdapterCatalog.Execute(new BridgeExecutionContext(initial, null, () => false)).Status,
                "initial provider did not execute");

            File.Delete(Path.Combine(adapters, "fixture.same.manifest.json"));
            WriteGeneration(adapters, bytes, identity, hash, "same", "2026-05-01T00:00:00Z", "FIXTURE_ECHO",
                1, 10, commandMode: "PersistentMutation");
            BridgeAdapterCatalog.IndexSynchronouslyForTests(Array.Empty<string>());
            Check(BridgeAdapterCatalog.Describe("FIXTURE_ECHO") == null,
                "changed same-generation command contract remained active");
            BridgeResult health = BridgeAdapterCatalog.Health();
            Check(health.Warnings.Any(item => item.Contains("publish a new generation")),
                "changed generation was not reported");
            Check(health.Lines.Any(item => item.Contains("changed without a new generation")),
                "loaded generation was not retained in health output");
        }
        finally
        {
            try { Directory.Delete(root, true); } catch { }
        }
    }

    private static void MissingFeaturePrerequisiteBlocked()
    {
        string path = WritePendingSuite("missing-adapter", "<FeatureTestSuite id=\"missing-adapter\" " +
            "mod=\"Fixture\" feature=\"Blocked\" requiredAdapters=\"not-installed\">" +
            "<Test name=\"blocked\"><Action><Call id=\"action\" command=\"MISSING_COMMAND\" />" +
            "</Action></Test></FeatureTestSuite>");
        try
        {
            BridgeRequest request = Request("blocked", "s");
            request.Command = "RUN_FEATURE_TESTS";
            request.AllowExpensive = true;
            BridgeCommandDescriptor descriptor = BridgeFeatureTests.Describe(request);
            request.Mode = descriptor.Mode;
            request.Cost = descriptor.Cost;
            Check(BridgeFeatureTests.Prepare(request) == null, "blocked plan failed preparation");
            BridgeResult result = BridgeFeatureTests.Execute(new BridgeExecutionContext(request, null, () => false));
            Equal(BridgeStatus.BLOCKED, result.Status, "missing adapter status");
            Check(File.Exists(path), "blocked suite did not remain pending");
        }
        finally { if (File.Exists(path)) File.Delete(path); }
    }

    private static void TypedFeatureAssertions()
    {
        string path = WritePendingSuite("typed-assertions", "<FeatureTestSuite id=\"typed-assertions\" " +
            "mod=\"Fixture\" feature=\"Typed\"><Test name=\"typed\" mutation=\"read\">" +
            "<Action><Call id=\"action\" command=\"FIXTURE_ECHO\" argument=\"hello\" /></Action>" +
            "<Assertions><Status step=\"action\" value=\"OK\"/><Schema step=\"action\" value=\"fixture.echo\"/>" +
            "<SchemaVersion step=\"action\" value=\"1\"/><Exact step=\"action\" field=\"value\" value=\"hello\"/>" +
            "<Boolean step=\"action\" field=\"flag\" value=\"true\"/>" +
            "<Member step=\"action\" field=\"members\" value=\"beta\"/><NoException step=\"action\"/>" +
            "</Assertions></Test></FeatureTestSuite>");
        BridgeRequest request = Request("typed", "s");
        request.Command = "RUN_FEATURE_TESTS";
        request.AllowExpensive = true;
        BridgeCommandDescriptor descriptor = BridgeFeatureTests.Describe(request);
        request.Mode = descriptor.Mode;
        request.Cost = descriptor.Cost;
        Check(BridgeFeatureTests.Prepare(request) == null, "typed plan failed preparation");
        BridgeResult result = BridgeFeatureTests.Execute(new BridgeExecutionContext(request, null, () => false));
        Equal(BridgeStatus.OK, result.Status, "typed assertion run");
        Check(!File.Exists(path), "passing suite remained pending");
    }

    private static string WritePendingSuite(string name, string xml)
    {
        string directory = Path.Combine(HarnessUserRoot, "FeatureTests", "Pending");
        Directory.CreateDirectory(directory);
        string path = Path.Combine(directory, name + ".xml");
        File.WriteAllText(path, xml);
        return path;
    }

    private static void BatchDerivesWriteMode()
    {
        BridgeRequest request = Request("batch", "s");
        request.Command = "BATCH";
        request.Argument = "STATUS;SET_SPEED:1";
        BridgeCommandDescriptor descriptor = BridgeDispatch.Describe(request);
        Equal(BridgeCommandMode.Reversible, descriptor.Mode, "batch write hidden");
        Equal(BridgeCostClass.Trivial, descriptor.Cost, "batch cost");
        Check(BridgeDispatch.Prepare(request) == null, "batch prepare");
    }

    private static void MacroDerivesWriteMode()
    {
        string root = NewMacroRoot("<BridgeCommands version=\"2\">" +
            "<Command name=\"INNER\"><Call command=\"SET_SPEED\" argument=\"${speed}\" /></Command>" +
            "<Command name=\"OUTER\"><Call command=\"STATUS\" /><Call command=\"INNER\" /></Command>" +
            "<Command name=\"ASSERT_TYPED\"><Call command=\"FIXTURE_ECHO\" argument=\"ok\" />" +
            "<Assert step=\"0\" status=\"OK\" schema=\"fixture.echo\" field=\"value\" equals=\"ok\" />" +
            "</Command>" +
            "<Command name=\"ASSERT_OUTER\"><Call command=\"ASSERT_TYPED\" /></Command>" +
            "</BridgeCommands>");
        try
        {
            BridgeOrchestration.Reload();
            BridgeRequest request = Request("macro", "s");
            request.Command = "OUTER";
            request.Argument = "speed=1";
            BridgeCommandDescriptor descriptor = BridgeDispatch.Describe(request);
            Equal(BridgeCommandMode.Reversible, descriptor.Mode, "macro write hidden");
            Check(BridgeDispatch.Prepare(request) == null, "macro prepare");
            BridgeResult dry = BridgeOrchestration.Execute(new BridgeExecutionContext(new BridgeRequest
            {
                RequestId = "dry", SessionId = "s", Command = "MACRO_DRY_RUN", Argument = "name=OUTER&speed=2",
                EnqueuedUtc = DateTime.UtcNow, DeadlineUtc = DateTime.UtcNow.AddSeconds(5)
            }, null, () => false));
            Equal(BridgeStatus.OK, dry.Status, "dry run");
            Check(dry.Lines.Any(line => line.Contains("SET_SPEED") && line.Contains("argument:2")), "parameter expansion");
            BridgeRequest asserted = Request("assert", "s");
            asserted.Command = "ASSERT_OUTER";
            asserted.AllowExpensive = true;
            BridgeCommandDescriptor assertedDescriptor = BridgeDispatch.Describe(asserted);
            asserted.Mode = assertedDescriptor.Mode;
            asserted.Cost = assertedDescriptor.Cost;
            Check(BridgeDispatch.Prepare(asserted) == null, "assert macro prepare");
            BridgeResult assertedResult = BridgeOrchestration.Execute(new BridgeExecutionContext(asserted, null,
                () => false));
            Equal(BridgeStatus.OK, assertedResult.Status, "typed macro assertion");
            Check(assertedResult.Lines.Any(line => line.Contains("assertion=step:0 status:OK")),
                "typed macro assertion evidence");
        }
        finally { ResetMacroRoot(root); }
    }

    private static void MacroCycleQuarantined()
    {
        string root = NewMacroRoot("<BridgeCommands version=\"2\">" +
            "<Command name=\"A\"><Call command=\"B\" /></Command>" +
            "<Command name=\"B\"><Call command=\"A\" /></Command>" +
            "<Command name=\"VALID\"><Call command=\"STATUS\" /></Command></BridgeCommands>");
        try
        {
            BridgeOrchestration.Reload();
            Check(BridgeOrchestration.Describe("A", string.Empty) == null, "cycle stayed active");
            Check(BridgeOrchestration.Describe("VALID", string.Empty) != null, "valid macro was quarantined");
            BridgeResult status = BridgeOrchestration.Execute(new BridgeExecutionContext(new BridgeRequest
            {
                RequestId = "status", SessionId = "s", Command = "MACRO_STATUS", Argument = string.Empty,
                EnqueuedUtc = DateTime.UtcNow, DeadlineUtc = DateTime.UtcNow.AddSeconds(5)
            }, null, () => false));
            Check(status.Warnings.Any(value => value.Contains("cycle")), "cycle not reported");
        }
        finally { ResetMacroRoot(root); }
    }

    private static string NewMacroRoot(string xml)
    {
        string root = Path.Combine(Path.GetTempPath(), "RimWorldDevBridgeMacros-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(root);
        BridgePaths.SetUserRootForTests(root);
        File.WriteAllText(BridgePaths.MacroPath, xml);
        return root;
    }

    private static void ResetMacroRoot(string root)
    {
        BridgePaths.SetUserRootForTests(HarnessUserRoot);
        BridgeOrchestration.Reload();
        try { Directory.Delete(root, true); } catch { }
    }
    private static AdapterManifest ReadManifest(string path)
    {
        using (FileStream stream = File.OpenRead(path))
        {
            return (AdapterManifest)new DataContractJsonSerializer(typeof(AdapterManifest)).ReadObject(stream);
        }
    }

    private static void WriteManifest(string path, AdapterManifest manifest)
    {
        using (FileStream stream = File.Create(path))
        {
            new DataContractJsonSerializer(typeof(AdapterManifest)).WriteObject(stream, manifest);
        }
    }

    private static void RestoreFixtureAdapterCatalog()
    {
        string built = Path.Combine(AppDomain.CurrentDomain.BaseDirectory, "BridgeFixtureAdapter.dll");
        if (!File.Exists(built))
            built = Path.GetFullPath(Path.Combine(AppDomain.CurrentDomain.BaseDirectory,
                "..", "..", "..", "FixtureAdapter", "bin", "Release", "net472", "BridgeFixtureAdapter.dll"));
        byte[] bytes = File.ReadAllBytes(built);
        string adapters = Path.Combine(HarnessUserRoot, "DevTools", "HotAdapters");
        Directory.CreateDirectory(adapters);
        foreach (string existing in Directory.GetFiles(adapters))
            File.Delete(existing);
        WriteGeneration(adapters, bytes, AssemblyName.GetAssemblyName(built).FullName, Sha256(bytes),
            "restore", "2026-01-01T00:00:00Z", "FIXTURE_ECHO", 1, 10);
        BridgePaths.Initialize(HarnessUserRoot);
        BridgeAdapterCatalog.IndexSynchronouslyForTests(Array.Empty<string>());
    }

    private static void WriteGeneration(string directory, byte[] bytes, string identity, string hash,
        string generation, string buildUtc, string command, int protocolMin, int protocolMax,
        string adapterId = "fixture", string providerType = "BridgeFixtureAdapter.FixtureProvider",
        string commandMode = "PureRead", string executionContract = null, string requiredPackageId = null)
    {
        string file = adapterId + "." + generation + ".dll";
        File.WriteAllBytes(Path.Combine(directory, file), bytes);
        AdapterManifest manifest = new AdapterManifest
        {
            manifestVersion = 1,
            adapterId = adapterId,
            displayName = "Fixture",
            version = "1.0.0",
            generation = generation,
            buildUtc = buildUtc,
            assemblyFile = file,
            assemblyIdentity = identity,
            assemblyBytes = bytes.Length,
            contentHash = hash,
            providerType = providerType,
            executionContract = executionContract,
            protocolMin = protocolMin,
            protocolMax = protocolMax,
            commands = new List<AdapterCommandManifest>
            {
                new AdapterCommandManifest
                {
                    name = command,
                    description = "fixture",
                    mode = commandMode,
                    cost = "Trivial",
                    requiresMap = false,
                    argumentSchema = "string",
                    resultSchema = "fixture.echo",
                    schemaVersion = 1,
                    minimumExecutionBudgetMs = 25
                }
            },
            requiredPackageIds = string.IsNullOrWhiteSpace(requiredPackageId)
                ? new List<string>() : new List<string> { requiredPackageId },
            optionalPackageIds = new List<string>(),
            changeSummary = "fixture"
        };
        string path = Path.Combine(directory, adapterId + "." + generation + ".manifest.json");
        using (FileStream stream = new FileStream(path, FileMode.CreateNew, FileAccess.Write, FileShare.None))
            new DataContractJsonSerializer(typeof(AdapterManifest)).WriteObject(stream, manifest);
    }

    private static void LegacyValidMetric()
    {
        Equal(BridgeStatus.OK, BridgeResult.FromLegacy(new[] { "invalidObjects=0", "valid=True" }).Status,
            "metric misclassified");
    }

    private static string Sha256(byte[] bytes)
    {
        using (SHA256 algorithm = SHA256.Create())
            return string.Concat(algorithm.ComputeHash(bytes).Select(value => value.ToString("X2")));
    }

    private static BridgeQuerySnapshot CreateSnapshot(string session, string command, string scope, int mapId,
        params int[] ids) => CreateSnapshot(session, command, scope, mapId, Rows(ids));

    private static BridgeQuerySnapshot CreateSnapshot(string session, string command, string scope, int mapId,
        IEnumerable<BridgeQuerySnapshotRow> rows)
    {
        List<BridgeQuerySnapshotRow> materialized = rows.ToList();
        Check(BridgeQuerySnapshotStore.TryCreate(session, command, scope, "thingId:asc", mapId,
            materialized.Count, false, materialized, out BridgeQuerySnapshot snapshot,
            out BridgeResult failure), "create snapshot: " +
            (failure == null ? "unknown" : FieldValue(failure, "error")));
        return snapshot;
    }

    private static IEnumerable<BridgeQuerySnapshotRow> Rows(params int[] ids) =>
        (ids ?? new int[0]).Select(id => new BridgeQuerySnapshotRow(id, "id=" + id));

    private static void AssertContext(BridgeSessionContextSnapshot snapshot, string session, string context,
        bool representative, bool leaseActive, string leaseState)
    {
        Equal(session, snapshot.SessionId, "context session");
        Equal(context, snapshot.WriteContext, "context value");
        Equal(representative, snapshot.RepresentativePlayerBehavior, "representative behavior");
        Equal(leaseActive, snapshot.WriteLeaseActive, "lease active");
        Equal(leaseState, snapshot.LeaseState, "lease state");

        BridgeResult[] reports =
        {
            BridgeRuntime.AddSessionContext(BridgeResult.Ok("status"), snapshot),
            BridgeRuntime.AddSessionContext(BridgeResult.Ok("sync"), snapshot),
            BridgeRuntime.AddSessionContext(BridgeResult.Ok("session"), snapshot)
        };
        foreach (BridgeResult report in reports)
        {
            Equal(session, FieldValue(report, "session"), "reported session");
            Equal(context, FieldValue(report, "context"), "reported context");
            Equal(context, FieldValue(report, "writeContext"), "reported write context");
            Equal(representative.ToString().ToLowerInvariant(),
                FieldValue(report, "representativePlayerBehavior"), "reported representative behavior");
            Equal(leaseActive.ToString().ToLowerInvariant(), FieldValue(report, "writeLeaseActive"),
                "reported lease active");
            Equal(leaseState, FieldValue(report, "leaseState"), "reported lease state");
        }
    }

    private static void ExpireAllLeases(BridgeAuthorization authorization)
    {
        FieldInfo leasesField = typeof(BridgeAuthorization).GetField("leases",
            BindingFlags.Instance | BindingFlags.NonPublic);
        object leases = leasesField.GetValue(authorization);
        foreach (object entry in (System.Collections.IEnumerable)leases)
        {
            object lease = entry.GetType().GetProperty("Value").GetValue(entry, null);
            lease.GetType().GetField("ExpiresUtc", BindingFlags.Instance | BindingFlags.NonPublic |
                BindingFlags.Public).SetValue(lease, DateTime.UtcNow.AddSeconds(-1));
        }
    }

    private static void InvokeRotateSession(string prefix)
    {
        MethodInfo rotate = typeof(BridgeRuntime).GetMethod("RotateSession",
            BindingFlags.Static | BindingFlags.NonPublic);
        try { rotate.Invoke(null, new object[] { prefix }); }
        catch (TargetInvocationException exception) { throw exception.InnerException ?? exception; }
    }

    private static string FieldValue(BridgeResult result, string name)
    {
        return result.Data.Single(field => field.Name == name).Value;
    }

    private static string ResultDetails(BridgeResult result)
    {
        return string.Join(",", result.Data.Select(field => field.Name + "=" + field.Value));
    }

    private static BridgeRequest Request(string id, string session) => new BridgeRequest
    {
        RequestId = id,
        SessionId = session,
        Command = "STATUS",
        Argument = string.Empty,
        EnqueuedUtc = DateTime.UtcNow,
        DeadlineUtc = DateTime.UtcNow.AddSeconds(5)
    };

    private static void Run(string name, Action test)
    {
        try { test(); passed++; Console.WriteLine("PASS " + name); }
        catch (Exception exception) { failed++; Console.WriteLine("FAIL " + name + ": " + exception); }
    }

    private static void Check(bool value, string message)
    {
        if (!value) throw new InvalidOperationException(message);
    }

    private static void Equal<T>(T expected, T actual, string message)
    {
        if (!EqualityComparer<T>.Default.Equals(expected, actual))
            throw new InvalidOperationException(message + " expected=" + expected + " actual=" + actual);
    }

    private sealed class QueuedContext : SynchronizationContext
    {
        private readonly Queue<Action> calls = new Queue<Action>();
        public override void Post(SendOrPostCallback callback, object state) => calls.Enqueue(() => callback(state));
        internal void Drain() { while (calls.Count > 0) calls.Dequeue()(); }
    }
}
