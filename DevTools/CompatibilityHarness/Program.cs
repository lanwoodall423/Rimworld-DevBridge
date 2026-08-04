using System;
using System.Collections.Generic;
using System.Linq;
using System.IO;
using System.Reflection;
using System.Security.Cryptography;
using System.Runtime.Serialization.Json;
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
        Run("session context transitions", SessionContextTransitions);
        Run("bridge indicator state transitions", BridgeIndicatorStateTransitions);
        Run("wake signal idempotence", WakeSignalIdempotence);
        Run("main thread dispatch queue", MainThreadDispatchQueue);
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
        Run("manifest adapter lifecycle", ManifestAdapterLifecycle);
        Run("missing feature prerequisite blocked", MissingFeaturePrerequisiteBlocked);
        Run("typed feature assertions", TypedFeatureAssertions);
        Run("batch derives transitive write mode", BatchDerivesWriteMode);
        Run("macro derives transitive write mode", MacroDerivesWriteMode);
        Run("macro cycle quarantined", MacroCycleQuarantined);
        Run("manifest generation reuse rejected", ManifestGenerationReuseRejected);
        Run("slow legacy adapter circuit breaker", SlowLegacyAdapterCircuitBreaker);
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
        BridgeRuntime.DrainMainThreadForTests();
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

        Check(BridgeRuntime.DrainMainThreadForTests() >= 1, "null transition publication drain");
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
        BridgeRuntime.DrainMainThreadForTests();
        Equal(newestSequence, BridgeRuntime.PublishedLifecycleSequenceForTests,
            "older lifecycle callback published after newer transition");
        Equal(Thread.CurrentThread.ManagedThreadId, BridgeRuntime.PublishedLifecycleThreadIdForTests,
            "rapid lifecycle publication ran off owner thread");
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
        MethodInfo complete = typeof(BridgeRuntime).GetMethod("CompleteScheduled",
            BindingFlags.Static | BindingFlags.NonPublic);
        complete.Invoke(null, new object[] { request,
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
    private static void WriteGeneration(string directory, byte[] bytes, string identity, string hash,
        string generation, string buildUtc, string command, int protocolMin, int protocolMax,
        string adapterId = "fixture", string providerType = "BridgeFixtureAdapter.FixtureProvider",
        string commandMode = "PureRead", string executionContract = null)
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
            requiredPackageIds = new List<string>(),
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
