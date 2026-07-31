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
        Run("scheduler cancellation", SchedulerCancellation);
        Run("expired queued request never executes", ExpiredQueuedRequest);
        Run("scheduler stale session", SchedulerStaleSession);
        Run("scheduler queue capacity", SchedulerQueueCapacity);
        Run("main thread dispatch queue", MainThreadDispatchQueue);
        Run("scheduler completion timing", SchedulerCompletionTiming);
        Run("in-flight cancellation", InFlightCancellation);
        Run("write lease and idempotency", WriteLeaseAndIdempotency);
        Run("idempotency conflict", IdempotencyConflict);
        Run("pre-execution failure not cached", PreExecutionFailureNotCached);
        Run("manifest adapter lifecycle", ManifestAdapterLifecycle);
        Run("missing feature prerequisite blocked", MissingFeaturePrerequisiteBlocked);
        Run("typed feature assertions", TypedFeatureAssertions);
        Run("batch derives transitive write mode", BatchDerivesWriteMode);
        Run("macro derives transitive write mode", MacroDerivesWriteMode);
        Run("macro cycle quarantined", MacroCycleQuarantined);
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
        BridgeResult result = BridgeResult.Ok().Add("large", new string('x', 20000));
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
        string cursor = BridgeCursor.Encode("s", "THINGS", first.CursorScope, 1);
        BridgeQuery same = BridgeQuery.Parse("filter=x&fields=id,label&cursor=" +
            Uri.EscapeDataString(cursor), "s", "THINGS", out failure);
        Check(failure == null && same.Offset == 1, "same fields rejected");
        BridgeQuery changed = BridgeQuery.Parse("filter=x&fields=id&cursor=" +
            Uri.EscapeDataString(cursor), "s", "THINGS", out failure);
        Check(changed == null && failure.Status == BridgeStatus.INVALID_ARGUMENT,
            "cursor allowed changed fields");
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
        string adapterId = "fixture")
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
            providerType = "BridgeFixtureAdapter.FixtureProvider",
            protocolMin = protocolMin,
            protocolMax = protocolMax,
            commands = new List<AdapterCommandManifest>
            {
                new AdapterCommandManifest
                {
                    name = command,
                    description = "fixture",
                    mode = "PureRead",
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
