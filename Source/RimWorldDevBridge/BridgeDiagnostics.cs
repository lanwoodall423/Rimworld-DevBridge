using System;
using System.Collections;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Linq;
using System.Reflection;
using System.Threading;
using HarmonyLib;
using RimWorld;
using UnityEngine;
using Verse;
using Verse.AI;

namespace RimWorldDevBridge
{
    internal static class BridgeDiagnostics
    {
        private static readonly object PerformanceGate = new object();
        private const int MaximumScannedObjects = BridgeQuerySnapshotStore.DefaultMaximumRows;
        private static DateTime previousPerformanceUtc;
        private static int previousPerformanceTick = -1;
        internal static int LastProjectionMaxItemsForTests => BridgeSnapshotProjection.LastMaxItemsForTests;
        internal static double LastProjectionMaxStepMsForTests => BridgeSnapshotProjection.LastMaxStepMsForTests;

        internal static void ResetProjectionMetricsForTests()
        {
            BridgeSnapshotProjection.ResetMetricsForTests();
        }
        internal delegate void RegisterCommand(string name, string description, BridgeCommandMode mode,
            BridgeCostClass cost, bool requiresMap, string argumentSchema);

        internal static void Register(RegisterCommand register) => BridgeDiagnosticCommands.Register(register);

        internal static BridgeResult Execute(BridgeExecutionContext context) => BridgeDiagnosticCommands.Execute(context);

        internal static BridgeResult Prepare(BridgeRequest request) => BridgeDiagnosticCommands.Prepare(request);

        internal static BridgeResult Pawns(BridgeExecutionContext context)
        {
            BridgeQuery query = Query(context.Request, out BridgeResult failure);
            if (failure != null) return failure;
            BridgeSnapshotProjection.Operation<Pawn> pending = context.Request.CooperativeState as
                BridgeSnapshotProjection.Operation<Pawn>;
            if (pending != null) return pending.Step(context);
            int mapId = context.Map.uniqueID;
            IReadOnlyList<Pawn> all = query.SnapshotId == null ? context.Map.mapPawns.AllPawnsSpawned : null;
            int available = all?.Count ?? 0;
            if (all != null && available > BridgeQuerySnapshotStore.MaximumRows)
                return SnapshotScanLimit(available);
            if (query.SnapshotId == null && !BridgeQuerySnapshotStore.CanCreate(out failure)) return failure;
            BridgeQuerySnapshot snapshot;
            if (query.SnapshotId != null)
            {
                if (!ResolveSnapshot(context, query, mapId, 0, Stopwatch.GetTimestamp(), null,
                    out snapshot, out failure)) return failure;
                return SnapshotPage("core.pawns", context.Request, query, snapshot);
            }
            context.Request.CooperativeState = new BridgeSnapshotProjection.Operation<Pawn>(context.Request,
                query, mapId, available,
                index => BridgeGameState.CurrentMap.mapPawns.AllPawnsSpawned[index],
                () => BridgeGameState.CurrentMap?.mapPawns.AllPawnsSpawned.Count ?? 0,
                pawn => pawn.thingIDNumber,
                pawn => string.IsNullOrWhiteSpace(query.Filter) ||
                    Matches(query.Filter, pawn.def?.defName, pawn.LabelShortCap),
                pawn => new BridgeQuerySnapshotRow(pawn.thingIDNumber,
                    PawnLine(pawn, context.SessionId, mapId)), "core.pawns");
            return ((BridgeSnapshotProjection.Operation<Pawn>)context.Request.CooperativeState).Step(context);
        }

        internal static BridgeResult Pawn(BridgeExecutionContext context)
        {
            if (!TryThingReference(context, context.Request.Argument, out int id, out BridgeResult failure)) return failure;
            Pawn pawn = context.Map.mapPawns.AllPawnsSpawned.FirstOrDefault(value => value.thingIDNumber == id);
            if (pawn == null) return BridgeResult.Fail(BridgeStatus.NOT_FOUND, "pawn_not_found").Add("thingId", id);
            BridgeResult result = BridgeResult.Ok("core.pawn")
                .Add("ref", Reference(context.SessionId, context.Map.uniqueID, id))
                .Add("thingId", id).Add("def", pawn.def?.defName).Add("label", pawn.LabelShortCap)
                .Add("position", Cell(pawn.Position)).Add("faction", pawn.Faction?.def?.defName ?? "none")
                .Add("gender", pawn.gender).Add("ageYears", pawn.ageTracker?.AgeBiologicalYearsFloat ?? 0f)
                .Add("downed", pawn.Downed).Add("dead", pawn.Dead).Add("drafted", pawn.Drafted)
                .Add("mentalState", pawn.MentalStateDef?.defName ?? "none");
            AddJob(result, pawn);
            AddComps(result, pawn);
            return result;
        }

        internal static BridgeResult Things(BridgeExecutionContext context)
        {
            BridgeQuery query = Query(context.Request, out BridgeResult failure);
            if (failure != null) return failure;
            BridgeSnapshotProjection.Operation<Thing> pending = context.Request.CooperativeState as
                BridgeSnapshotProjection.Operation<Thing>;
            if (pending != null) return pending.Step(context);
            int mapId = context.Map.uniqueID;
            IReadOnlyList<Thing> all = query.SnapshotId == null ? context.Map.listerThings.AllThings : null;
            int available = all?.Count ?? 0;
            if (all != null && available > BridgeQuerySnapshotStore.MaximumRows)
                return SnapshotScanLimit(available);
            if (query.SnapshotId == null && !BridgeQuerySnapshotStore.CanCreate(out failure)) return failure;
            BridgeQuerySnapshot snapshot;
            if (query.SnapshotId != null)
            {
                if (!ResolveSnapshot(context, query, mapId, 0, Stopwatch.GetTimestamp(), null,
                    out snapshot, out failure)) return failure;
                return SnapshotPage("core.things", context.Request, query, snapshot);
            }
            context.Request.CooperativeState = new BridgeSnapshotProjection.Operation<Thing>(context.Request,
                query, mapId, available,
                index => BridgeGameState.CurrentMap.listerThings.AllThings[index],
                () => BridgeGameState.CurrentMap?.listerThings.AllThings.Count ?? 0,
                thing => thing.thingIDNumber,
                thing => string.IsNullOrWhiteSpace(query.Filter) || Matches(query.Filter,
                    thing.def?.defName, thing.LabelShortCap, thing.GetType().FullName),
                thing => new BridgeQuerySnapshotRow(thing.thingIDNumber,
                    ThingLine(thing, context.SessionId, mapId)), "core.things");
            return ((BridgeSnapshotProjection.Operation<Thing>)context.Request.CooperativeState).Step(context);
        }

        internal static BridgeResult Thing(BridgeExecutionContext context)
        {
            if (!TryThingReference(context, context.Request.Argument, out int id, out BridgeResult failure)) return failure;
            Thing thing = context.Map.listerThings.AllThings.FirstOrDefault(value => value.thingIDNumber == id);
            if (thing == null) return BridgeResult.Fail(BridgeStatus.NOT_FOUND, "thing_not_found").Add("thingId", id);
            BridgeResult result = BridgeResult.Ok("core.thing")
                .Add("ref", Reference(context.SessionId, context.Map.uniqueID, id))
                .Add("thingId", id).Add("type", thing.GetType().FullName).Add("def", thing.def?.defName)
                .Add("label", thing.LabelShortCap).Add("spawned", thing.Spawned)
                .Add("position", thing.Spawned ? Cell(thing.Position) : "unspawned")
                .Add("rotation", thing.Rotation).Add("stackCount", thing.stackCount)
                .Add("faction", thing.Faction?.def?.defName ?? "none");
            if (thing.def?.useHitPoints == true) result.Add("hitPoints", thing.HitPoints).Add("maxHitPoints", thing.MaxHitPoints);
            AddComps(result, thing);
            return result;
        }

        internal static BridgeResult Defs(BridgeExecutionContext context)
        {
            Dictionary<string, string> options = BridgeProtocol.ParseOptions((context.Request.Argument ?? string.Empty).Replace(';', '&'));
            string kind = (BridgeProtocol.Value(options, "kind") ?? "thing").Trim().ToLowerInvariant();
            IEnumerable<Def> source;
            switch (kind)
            {
                case "job": source = DefDatabase<JobDef>.AllDefsListForReading; break;
                case "hediff": source = DefDatabase<HediffDef>.AllDefsListForReading; break;
                case "research": source = DefDatabase<ResearchProjectDef>.AllDefsListForReading; break;
                case "designation": source = DefDatabase<DesignationDef>.AllDefsListForReading; break;
                case "thing": source = DefDatabase<ThingDef>.AllDefsListForReading; break;
                default: return BridgeResult.Fail(BridgeStatus.INVALID_ARGUMENT, "unsupported_def_kind",
                    "Use thing, job, hediff, research, or designation.");
            }
            BridgeQuery query = Query(context.Request, out BridgeResult failure);
            if (failure != null) return failure;
            List<Def> scanned = source.Take(MaximumScannedObjects + 1).ToList();
            bool scanTruncated = scanned.Count > MaximumScannedObjects;
            List<Def> values = scanned.Take(MaximumScannedObjects)
                .Where(def => Matches(query.Filter, def.defName, def.label, def.GetType().FullName))
                .OrderBy(def => def.defName, StringComparer.Ordinal).ToList();
            BridgeResult result = Paged("core.defs", context.Request, query, values.Count).Add("kind", kind)
                .Add("scanned", Math.Min(scanned.Count, MaximumScannedObjects));
            foreach (Def def in values.Skip(query.Offset).Take(query.Limit))
                result.AddLine("def=name:" + BridgeText.Clean(def.defName) + " type:" +
                    BridgeText.Clean(def.GetType().FullName) + " label:" + BridgeText.Clean(def.label) +
                    " mod:" + BridgeText.Clean(def.modContentPack?.PackageIdPlayerFacing));
            FinishPage(result, context.Request, query, values.Count);
            ApplyScanLimit(result, scanTruncated);
            return result;
        }

        internal static BridgeResult Components(BridgeExecutionContext context)
        {
            BridgeQuery query = Query(context.Request, out BridgeResult failure);
            if (failure != null) return failure;
            List<ComponentRef> values = ComponentRefs()
                .Where(item => Matches(query.Filter, item.Scope, item.Value.GetType().FullName))
                .OrderBy(item => item.Scope).ThenBy(item => item.Index).ToList();
            BridgeResult result = Paged("core.components", context.Request, query, values.Count);
            foreach (ComponentRef item in values.Skip(query.Offset).Take(query.Limit))
                result.AddLine("component=ref:" + context.SessionId + ":" + item.Scope + ":" +
                    item.Index + " type:" +
                    BridgeText.Clean(item.Value.GetType().FullName));
            FinishPage(result, context.Request, query, values.Count);
            return result;
        }

        internal static BridgeResult Component(BridgeExecutionContext context)
        {
            string value = (context.Request.Argument ?? string.Empty).Trim();
            string[] reference = value.Split(':');
            if (reference.Length == 3)
            {
                if (!string.Equals(reference[0], context.SessionId, StringComparison.Ordinal))
                    return BridgeResult.Fail(BridgeStatus.INCOMPATIBLE, "stale_component_reference");
                value = reference[1] + ":" + reference[2];
            }
            ComponentRef target = ComponentRefs().FirstOrDefault(item =>
                string.Equals(item.Scope + ":" + item.Index, value, StringComparison.OrdinalIgnoreCase) ||
                item.Value.GetType().FullName.IndexOf(value, StringComparison.OrdinalIgnoreCase) >= 0);
            if (target == null) return BridgeResult.Fail(BridgeStatus.NOT_FOUND, "component_not_found");
            BridgeResult result = BridgeResult.Ok("core.component").Add("ref", context.SessionId + ":" +
                target.Scope + ":" + target.Index)
                .Add("type", target.Value.GetType().FullName);
            foreach (FieldInfo field in target.Value.GetType().GetFields(BindingFlags.Instance | BindingFlags.Public)
                .Where(field => SafeFieldType(field.FieldType)).OrderBy(field => field.Name).Take(48))
            {
                try { result.Add("field." + field.Name, Simple(field.GetValue(target.Value))); }
                catch (Exception exception) { result.Warn(field.Name + ": " + exception.GetBaseException().Message); }
            }
            return result;
        }

        internal static BridgeResult Jobs(BridgeExecutionContext context)
        {
            BridgeQuery query = Query(context.Request, out BridgeResult failure);
            if (failure != null) return failure;
            BridgeSnapshotProjection.Operation<Pawn> pending = context.Request.CooperativeState as
                BridgeSnapshotProjection.Operation<Pawn>;
            if (pending != null) return pending.Step(context);
            int mapId = context.Map.uniqueID;
            IReadOnlyList<Pawn> all = query.SnapshotId == null ? context.Map.mapPawns.AllPawnsSpawned : null;
            int available = all?.Count ?? 0;
            if (all != null && available > BridgeQuerySnapshotStore.MaximumRows)
                return SnapshotScanLimit(available);
            if (query.SnapshotId == null && !BridgeQuerySnapshotStore.CanCreate(out failure)) return failure;
            BridgeQuerySnapshot snapshot;
            if (query.SnapshotId != null)
            {
                if (!ResolveSnapshot(context, query, mapId, 0, Stopwatch.GetTimestamp(), null,
                    out snapshot, out failure)) return failure;
                return SnapshotPage("core.jobs", context.Request, query, snapshot);
            }
            context.Request.CooperativeState = new BridgeSnapshotProjection.Operation<Pawn>(context.Request,
                query, mapId, available,
                index => BridgeGameState.CurrentMap.mapPawns.AllPawnsSpawned[index],
                () => BridgeGameState.CurrentMap?.mapPawns.AllPawnsSpawned.Count ?? 0,
                pawn => pawn.thingIDNumber,
                pawn => pawn.CurJob != null && (string.IsNullOrWhiteSpace(query.Filter) ||
                    Matches(query.Filter, pawn.CurJobDef?.defName, pawn.LabelShortCap)),
                pawn => new BridgeQuerySnapshotRow(pawn.thingIDNumber,
                    "job=pawnRef:" + Reference(context.SessionId, mapId, pawn.thingIDNumber) +
                    " pawn:" + BridgeText.Clean(pawn.LabelShortCap) +
                    " def:" + BridgeText.Clean(pawn.CurJobDef?.defName) +
                    " targetA:" + Target(pawn.CurJob.targetA) + " startTick:" + pawn.CurJob.startTick +
                    " playerForced:" + pawn.CurJob.playerForced), "core.jobs");
            return ((BridgeSnapshotProjection.Operation<Pawn>)context.Request.CooperativeState).Step(context);
        }

        internal static BridgeResult Designations(BridgeExecutionContext context)
        {
            BridgeQuery query = Query(context.Request, out BridgeResult failure);
            if (failure != null) return failure;
            List<Designation> all = context.Map.designationManager.AllDesignations;
            bool scanTruncated = all.Count > MaximumScannedObjects;
            List<Designation> values = all.Take(MaximumScannedObjects)
                .Where(item => string.IsNullOrWhiteSpace(query.Filter) ||
                    Matches(query.Filter, item.def?.defName, item.target.ToString()))
                .OrderBy(item => item.def?.defName).ThenBy(item => item.target.ToString()).ToList();
            BridgeResult result = Paged("core.designations", context.Request, query, values.Count);
            foreach (Designation item in values.Skip(query.Offset).Take(query.Limit))
                result.AddLine("designation=def:" + BridgeText.Clean(item.def?.defName) + " target:" +
                    BridgeText.Clean(item.target.ToString()));
            FinishPage(result, context.Request, query, values.Count);
            ApplyScanLimit(result, scanTruncated);
            return result;
        }

        internal static BridgeResult Selected()
        {
            Thing thing = Find.Selector?.SingleSelectedThing;
            if (thing == null) return BridgeResult.Ok("core.selected").Add("selected", "none");
            int mapId = thing.Map?.uniqueID ?? -1;
            return BridgeResult.Ok("core.selected").Add("selected", "thing")
                .Add("ref", Reference(BridgeRuntime.SessionId, mapId, thing.thingIDNumber))
                .Add("thingId", thing.thingIDNumber).Add("type", thing.GetType().FullName)
                .Add("def", thing.def?.defName).Add("label", thing.LabelShortCap)
                .Add("position", thing.Spawned ? Cell(thing.Position) : "unspawned");
        }

        internal static BridgeResult UiState(BridgeExecutionContext context)
        {
            BridgeQuery query = Query(context.Request, out BridgeResult failure);
            if (failure != null) return failure;
            List<Window> windows = (Find.WindowStack?.Windows ?? new List<Window>()).OrderBy(window => window.ID).ToList();
            BridgeResult result = Paged("core.uiState", context.Request, query, windows.Count)
                .Add("screenWidth", Screen.width).Add("screenHeight", Screen.height).Add("uiScale", Prefs.UIScale)
                .Add("windowCount", windows.Count);
            foreach (Window window in windows.Skip(query.Offset).Take(query.Limit))
            {
                Rect rect = window.windowRect;
                bool clipped = rect.xMin < 0f || rect.yMin < 0f || rect.xMax > Screen.width || rect.yMax > Screen.height;
                result.AddLine("window=id:" + window.ID + " type:" + BridgeText.Clean(window.GetType().FullName) +
                    " title:" + BridgeText.Clean(window.optionalTitle) + " rect:" + RectValue(rect) +
                    " clipped:" + clipped + " layer:" + window.layer);
            }
            FinishPage(result, context.Request, query, windows.Count);
            return result;
        }

        internal static BridgeResult Select(BridgeExecutionContext context)
        {
            if (!TryThingReference(context, context.Request.Argument, out int id, out BridgeResult failure)) return failure;
            Thing thing = context.Map.listerThings.AllThings.FirstOrDefault(item => item.thingIDNumber == id);
            if (thing == null) return BridgeResult.Fail(BridgeStatus.NOT_FOUND, "thing_not_found");
            Find.Selector.ClearSelection();
            Find.Selector.Select(thing);
            if (thing.Spawned) Find.CameraDriver.JumpToCurrentMapLoc(thing.Position);
            return BridgeResult.Ok("core.select").Add("ref", Reference(context.SessionId, context.Map.uniqueID, id))
                .WithMutation("selected and focused thing " + id);
        }

        internal static BridgeResult Jump(BridgeExecutionContext context)
        {
            if (!TryCell(context.Request.Argument, context.Map, out IntVec3 cell, out BridgeResult failure)) return failure;
            Find.CameraDriver.JumpToCurrentMapLoc(cell);
            return BridgeResult.Ok("core.jump").Add("cell", Cell(cell)).WithMutation("camera jump " + Cell(cell));
        }

        internal static BridgeResult Screenshot(BridgeExecutionContext context)
        {
            string name = SafeArtifactName(context.Request.Argument, "screenshot-" + DateTime.UtcNow.ToString("yyyyMMdd-HHmmssfff")) + ".png";
            return CaptureScreenshot(new Rect(0f, 0f, Screen.width, Screen.height), Screen.width,
                Screen.height, name, "core.screenshot");
        }

        internal static BridgeResult ScreenshotRegion(BridgeExecutionContext context)
        {
            Dictionary<string, string> options;
            try { options = BridgeProtocol.ParseOptions((context.Request.Argument ?? string.Empty).Replace(';', '&')); }
            catch (Exception exception)
            {
                return BridgeResult.Fail(BridgeStatus.INVALID_ARGUMENT, "invalid_screenshot_options",
                    exception.GetBaseException().Message);
            }
            if (!int.TryParse(BridgeProtocol.Value(options, "x"), out int x) ||
                !int.TryParse(BridgeProtocol.Value(options, "y"), out int y) ||
                !int.TryParse(BridgeProtocol.Value(options, "width"), out int width) ||
                !int.TryParse(BridgeProtocol.Value(options, "height"), out int height) ||
                x < 0 || y < 0 || width < 1 || height < 1 || width > Screen.width ||
                height > Screen.height || x > Screen.width - width || y > Screen.height - height)
                return BridgeResult.Fail(BridgeStatus.INVALID_ARGUMENT, "invalid_screenshot_region");
            string name = SafeArtifactName(BridgeProtocol.Value(options, "name"),
                "region-" + DateTime.UtcNow.ToString("yyyyMMdd-HHmmssfff")) + ".png";
            return CaptureScreenshot(new Rect(x, y, width, height), width, height, name,
                "core.screenshotRegion").Add("x", x).Add("y", y);
        }

        private static BridgeResult CaptureScreenshot(Rect source, int width, int height, string name,
            string schema)
        {
            string path = BridgePaths.SafeOutputPath("Captures", name);
            Texture2D image = new Texture2D(width, height, TextureFormat.RGB24, false);
            try
            {
                image.ReadPixels(source, 0, 0, false);
                image.Apply(false, false);
                byte[] bytes = image.EncodeToPNG();
                File.WriteAllBytes(path, bytes);
                return BridgeResult.Ok(schema).Add("path", path).Add("bytes", bytes.Length)
                    .Add("sha256", BridgeDiagnosticArtifacts.Sha256(bytes)).Add("width", width).Add("height", height)
                    .WithMutation("wrote screenshot artifact");
            }
            finally { UnityEngine.Object.Destroy(image); }
        }

        internal static BridgeResult RefreshCell(BridgeExecutionContext context)
        {
            if (!TryCell(context.Request.Argument, context.Map, out IntVec3 cell, out BridgeResult failure)) return failure;
            context.Map.mapDrawer.MapMeshDirty(cell, ulong.MaxValue);
            return BridgeResult.Ok("core.refreshCell").Add("cell", Cell(cell))
                .WithMutation("marked map mesh dirty at " + Cell(cell));
        }

        internal static BridgeResult Logs(BridgeExecutionContext context, string category)
        {
            BridgeQuery query = Query(context.Request, out BridgeResult failure);
            if (failure != null) return failure;
            IEnumerable<LogMessage> source = Log.Messages ?? Enumerable.Empty<LogMessage>();
            if (category == "def") source = source.Where(item => item.type == LogMessageType.Error &&
                (Contains(item.text, "def") || Contains(item.text, "config")));
            else if (category == "patch") source = source.Where(item => item.type == LogMessageType.Error &&
                (Contains(item.text, "patch") || Contains(item.text, "harmony")));
            List<LogMessage> values = source.Where(item => Matches(query.Filter, item.text, item.StackTrace))
                .ToList();
            BridgeResult result = Paged("core.logs", context.Request, query, values.Count).Add("category", category ?? "all");
            foreach (var pair in values.Select((item, index) => new { item, index }).Skip(query.Offset).Take(query.Limit))
                result.AddLine("log=index:" + pair.index + " type:" + pair.item.type + " repeats:" + pair.item.repeats +
                    " text:" + BridgeText.Clean(pair.item.text));
            FinishPage(result, context.Request, query, values.Count);
            return result;
        }

        internal static BridgeResult HarmonyPatches(BridgeExecutionContext context)
        {
            BridgeQuery query = Query(context.Request, out BridgeResult failure);
            if (failure != null) return failure;
            List<MethodBase> methods = Harmony.GetAllPatchedMethods()
                .Where(method => Matches(query.Filter, method.DeclaringType?.FullName, method.Name))
                .OrderBy(method => method.DeclaringType?.FullName).ThenBy(method => method.Name).ToList();
            BridgeResult result = Paged("core.harmonyPatches", context.Request, query, methods.Count);
            foreach (MethodBase method in methods.Skip(query.Offset).Take(query.Limit))
            {
                Patches patches = Harmony.GetPatchInfo(method);
                result.AddLine("method=" + BridgeText.Clean(method.DeclaringType?.FullName) + "." + method.Name +
                    " owners:" + BridgeText.Clean(string.Join(",", patches == null
                        ? Enumerable.Empty<string>() : patches.Owners)) +
                    " prefixes:" + (patches?.Prefixes.Count ?? 0) + " postfixes:" + (patches?.Postfixes.Count ?? 0) +
                    " transpilers:" + (patches?.Transpilers.Count ?? 0) + " finalizers:" + (patches?.Finalizers.Count ?? 0));
            }
            FinishPage(result, context.Request, query, methods.Count);
            return result;
        }

        internal static BridgeResult CompatibilityReport()
        {
            int errors = (Log.Messages ?? Enumerable.Empty<LogMessage>()).Count(item => item.type == LogMessageType.Error);
            return BridgeResult.Ok("core.compatibilityReport")
                .Add("bridge", BridgeProtocol.BridgeVersion).Add("protocol", BridgeProtocol.ProtocolVersion)
                .Add("schema", BridgeProtocol.CoreSchema).Add("game", VersionControl.CurrentVersionStringWithoutBuild)
                .Add("loadedMods", LoadedModManager.RunningModsListForReading.Count)
                .Add("adapterIndex", BridgeAdapterCatalog.State).Add("adapterFingerprint", BridgeAdapterCatalog.Fingerprint)
                .Add("logErrors", errors).Add("devMode", Prefs.DevMode)
                .Add("remoteMutationEnabled", RimWorldDevBridgeMod.Settings?.RemoteMutationEnabled ?? false);
        }

        internal static BridgeResult CaptureState(BridgeExecutionContext context) =>
            BridgeDiagnosticArtifacts.CaptureState(context);

        internal static BridgeResult DiffState(BridgeRequest request) => BridgeDiagnosticArtifacts.DiffState(request);

        internal static BridgeResult Performance()
        {
            Process process = Process.GetCurrentProcess();
            try { process.Refresh(); } catch { }
            long workingSet = 0;
            long privateBytes = 0;
            int threadCount = 0;
            double processorMs = 0d;
            try
            {
                workingSet = process.WorkingSet64;
                privateBytes = process.PrivateMemorySize64;
                threadCount = process.Threads.Count;
                processorMs = process.TotalProcessorTime.TotalMilliseconds;
            }
            catch { }
            int tick = BridgeGameState.TickManager?.TicksGame ?? -1;
            DateTime now = DateTime.UtcNow;
            double sampleSeconds = 0d;
            int sampleTicks = 0;
            lock (PerformanceGate)
            {
                if (previousPerformanceTick >= 0 && tick >= previousPerformanceTick)
                {
                    sampleSeconds = (now - previousPerformanceUtc).TotalSeconds;
                    sampleTicks = tick - previousPerformanceTick;
                }
                previousPerformanceUtc = now;
                previousPerformanceTick = tick;
            }
            BridgeResult result = BridgeResult.Ok("core.performance")
                .Add("processId", process.Id).Add("managedBytes", GC.GetTotalMemory(false))
                .Add("workingSetBytes", workingSet).Add("privateBytes", privateBytes)
                .Add("threads", threadCount).Add("processorMs", processorMs)
                .Add("bridgeClients", BridgeRuntime.ActiveClients).Add("adapterIndex", BridgeAdapterCatalog.State)
                .Add("bootstrapMs", BridgeRuntime.BootstrapMs).Add("harmonyMs", BridgeRuntime.HarmonyMs)
                .Add("finalizeInitMs", BridgeRuntime.FinalizeInitMs).Add("activationMs", BridgeRuntime.ActivationMs)
                .Add("bootstrapManagedDeltaBytesApprox", BridgeRuntime.BootstrapManagedDeltaBytes)
                .Add("sampleSeconds", sampleSeconds).Add("sampleTicks", sampleTicks)
                .Add("sampleTicksPerSecond", sampleSeconds > 0d ? sampleTicks / sampleSeconds : 0d)
                .Add("captureDirectory", BridgePaths.CapturePath)
                .Add("captureFiles", Directory.Exists(BridgePaths.CapturePath)
                    ? Directory.GetFiles(BridgePaths.CapturePath).Length : 0);
            if (workingSet <= 0 || threadCount <= 0)
                result.Warn("Mono did not expose reliable OS process counters; managedBytes remains available.");
            return result;
        }

        internal static BridgeResult Benchmark(BridgeExecutionContext context)
        {
            int iterations = BridgeProtocol.ParseBoundedInt((context.Request.Argument ?? string.Empty).Replace("iterations=", ""),
                20, 1, 100);
            long start = Stopwatch.GetTimestamp();
            long checksum = 0;
            for (int i = 0; i < iterations; i++)
            {
                context.ThrowIfCancellationRequested();
                checksum += context.Map.listerThings.AllThings.Count;
                checksum += context.Map.mapPawns.AllPawnsSpawned.Count;
            }
            double elapsed = BridgeTiming.Milliseconds(start);
            return BridgeResult.Ok("core.benchmark").Add("iterations", iterations).Add("elapsedMs", elapsed)
                .Add("meanMs", elapsed / iterations).Add("checksum", checksum);
        }

        internal static BridgeResult SaveGame(string argument)
        {
            string name = SafeSaveName(argument);
            if (name == null) return BridgeResult.Fail(BridgeStatus.INVALID_ARGUMENT, "invalid_save_name");
            if (Current.Game == null) return BridgeResult.Fail(BridgeStatus.UNAVAILABLE, "game_required");
            GameDataSaveLoader.SaveGame(name);
            return BridgeResult.Ok("core.saveGame").Add("save", name).WithMutation("saved development copy " + name);
        }

        internal static BridgeResult LoadGame(string argument)
        {
            string name = SafeSaveName(argument);
            if (name == null) return BridgeResult.Fail(BridgeStatus.INVALID_ARGUMENT, "invalid_save_name");
            string path = GenFilePaths.FilePathForSavedGame(name);
            if (!File.Exists(path)) return BridgeResult.Fail(BridgeStatus.NOT_FOUND, "save_not_found").Add("save", name);
            GameDataSaveLoader.CheckVersionAndLoadGame(name);
            return BridgeResult.Ok("core.loadGame").Add("save", name).WithMutation("requested load of " + name);
        }

        private static List<ComponentRef> ComponentRefs()
        {
            List<ComponentRef> result = new List<ComponentRef>();
            AddComponents(result, "map", BridgeGameState.CurrentMap?.components);
            AddComponents(result, "game", Current.Game?.components);
            AddComponents(result, "world", BridgeGameState.World?.components);
            return result;
        }

        private static void AddComponents<T>(List<ComponentRef> result, string scope, IList<T> values)
        {
            if (values == null) return;
            for (int i = 0; i < values.Count; i++) if (values[i] != null)
                result.Add(new ComponentRef { Scope = scope, Index = i, Value = values[i] });
        }

        private static void AddComps(BridgeResult result, Thing thing)
        {
            ThingWithComps withComps = thing as ThingWithComps;
            if (withComps?.AllComps == null) return;
            foreach (ThingComp comp in withComps.AllComps.OrderBy(value => value.GetType().FullName).Take(64))
                result.AddLine("comp=type:" + BridgeText.Clean(comp.GetType().FullName) + " props:" +
                    BridgeText.Clean(comp.props?.GetType().FullName));
        }

        private static void AddJob(BridgeResult result, Pawn pawn)
        {
            Job job = pawn.CurJob;
            if (job == null) { result.Add("job", "none"); return; }
            result.Add("job", job.def?.defName).Add("jobTargetA", Target(job.targetA))
                .Add("jobTargetB", Target(job.targetB)).Add("jobStartTick", job.startTick)
                .Add("jobPlayerForced", job.playerForced);
        }

        private static string PawnLine(Pawn pawn, string session, int mapId) =>
            "pawn=ref:" + Reference(session, mapId, pawn.thingIDNumber) + " def:" +
            BridgeText.Clean(pawn.def?.defName) + " label:" + BridgeText.Clean(pawn.LabelShortCap) +
            " faction:" + BridgeText.Clean(pawn.Faction?.def?.defName) + " pos:" + Cell(pawn.Position) +
            " dead:" + pawn.Dead + " downed:" + pawn.Downed + " job:" + BridgeText.Clean(pawn.CurJobDef?.defName);

        private static string ThingLine(Thing thing, string session, int mapId) =>
            "thing=ref:" + Reference(session, mapId, thing.thingIDNumber) + " type:" +
            BridgeText.Clean(thing.GetType().FullName) + " def:" + BridgeText.Clean(thing.def?.defName) +
            " label:" + BridgeText.Clean(thing.LabelShortCap) + " pos:" +
            (thing.Spawned ? Cell(thing.Position) : "unspawned") + " stack:" + thing.stackCount;

        private static bool TryThingReference(BridgeExecutionContext context, string value, out int id,
            out BridgeResult failure)
        {
            id = 0;
            failure = null;
            string[] parts = (value ?? string.Empty).Trim().Split(':');
            if (parts.Length == 1 && int.TryParse(parts[0], out id)) return true;
            if (parts.Length != 3 || !int.TryParse(parts[1], out int mapId) || !int.TryParse(parts[2], out id))
            {
                failure = BridgeResult.Fail(BridgeStatus.INVALID_ARGUMENT, "invalid_object_reference");
                return false;
            }
            if (!string.Equals(parts[0], context.SessionId, StringComparison.Ordinal))
            {
                failure = BridgeResult.Fail(BridgeStatus.INCOMPATIBLE, "stale_object_reference");
                return false;
            }
            if (mapId != context.Map.uniqueID)
            {
                failure = BridgeResult.Fail(BridgeStatus.INCOMPATIBLE, "wrong_map_reference");
                return false;
            }
            return true;
        }

        private static bool TryCell(string value, Map map, out IntVec3 cell, out BridgeResult failure)
        {
            cell = IntVec3.Invalid;
            failure = null;
            string[] parts = (value ?? string.Empty).Split(',');
            if (parts.Length != 2 || !int.TryParse(parts[0], out int x) || !int.TryParse(parts[1], out int z))
            {
                failure = BridgeResult.Fail(BridgeStatus.INVALID_ARGUMENT, "invalid_cell", "Expected x,z.");
                return false;
            }
            cell = new IntVec3(x, 0, z);
            if (!cell.InBounds(map))
            {
                failure = BridgeResult.Fail(BridgeStatus.INVALID_ARGUMENT, "cell_out_of_bounds");
                return false;
            }
            return true;
        }

        private static bool ResolveSnapshot(BridgeExecutionContext context, BridgeQuery query, int mapId,
            int scanned, long snapshotStart, Func<List<BridgeQuerySnapshotRow>> project,
            out BridgeQuerySnapshot snapshot,
            out BridgeResult failure)
        {
            snapshot = null;
            failure = null;
            if (query.SnapshotId != null)
                return BridgeQuerySnapshotStore.TryGet(context.SessionId, context.Request.Command,
                    query.CursorScope, query.Ordering, mapId, query.SnapshotId, query.SnapshotExpiryTicks,
                    out snapshot, out failure);

            List<BridgeQuerySnapshotRow> rows;
            try
            {
                context.ThrowIfCancellationRequested();
                rows = project();
                context.ThrowIfCancellationRequested();
                CheckSnapshotBudget(snapshotStart);
            }
            catch (SnapshotBudgetExceededException)
            {
                failure = SnapshotTimeLimit();
                return false;
            }
            catch (SnapshotMemoryExceededException)
            {
                failure = SnapshotMemoryLimit();
                return false;
            }
            if (!BridgeQuerySnapshotStore.TryCreate(context.SessionId, context.Request.Command,
                query.CursorScope, query.Ordering, mapId, scanned, false, rows, out snapshot, out failure))
                return false;
            try { CheckSnapshotBudget(snapshotStart); }
            catch (SnapshotBudgetExceededException)
            {
                BridgeQuerySnapshotStore.Remove(snapshot.Id);
                snapshot = null;
                failure = SnapshotTimeLimit();
                return false;
            }
            query.SnapshotId = snapshot.Id;
            query.SnapshotExpiryTicks = snapshot.ExpiresUtc.Ticks;
            return true;
        }

        private static BridgeResult SnapshotScanLimit(int available)
        {
            BridgeResult result = BridgeResult.Fail(BridgeStatus.PARTIAL, "snapshot_scan_limit",
                "The live collection has " + available + " entries; narrow the filter before paging.")
                .Add("available", available).Add("maximumRows", BridgeQuerySnapshotStore.MaximumRows);
            result.Truncated = true;
            return result.Warn("query snapshot was not created because the bounded scan limit was reached");
        }

        internal static BridgeResult SnapshotTimeLimit()
        {
            BridgeResult result = BridgeResult.Fail(BridgeStatus.PARTIAL, "snapshot_time_limit",
                "The query snapshot exceeded the effective main-thread budget; narrow the filter.");
            result.Truncated = true;
            return result.Warn("query snapshot was not created within the main-thread budget");
        }

        internal static BridgeResult SnapshotMemoryLimit()
        {
            BridgeResult result = BridgeResult.Fail(BridgeStatus.BUSY, "snapshot_memory_limit",
                "The query snapshot exceeded the configured memory bound; narrow the filter.");
            result.Truncated = true;
            return result.Warn("query snapshot was not created within the memory bound");
        }

        internal static void AddSnapshotRow(List<BridgeQuerySnapshotRow> rows, BridgeQuerySnapshotRow row,
            ref long estimatedBytes)
        {
            estimatedBytes += row.EstimatedBytes;
            if (estimatedBytes > BridgeQuerySnapshotStore.AvailableBytes)
                throw new SnapshotMemoryExceededException();
            rows.Add(row);
        }

        internal static void CheckSnapshotBudget(long start)
        {
            int budgetMs = BridgeRuntime.EffectiveMainThreadBudgetMs;
            if (budgetMs > 0 && BridgeTiming.Milliseconds(start) > budgetMs)
                throw new SnapshotBudgetExceededException();
        }

        internal static BridgeResult SnapshotPage(string schema, BridgeRequest request, BridgeQuery query,
            BridgeQuerySnapshot snapshot)
        {
            int total = snapshot.Rows.Count;
            if (query.Offset > total)
                return BridgeResult.Fail(BridgeStatus.INVALID_ARGUMENT, "cursor_offset_invalid");
            BridgeResult result = Paged(schema, request, query, total)
                .Add("scanned", snapshot.Scanned).Add("available", snapshot.Available);
            int end = Math.Min(total, query.Offset + query.Limit);
            for (int index = query.Offset; index < end; index++) result.AddLine(snapshot.Rows[index].Line);
            FinishSnapshotPage(result, request, query, snapshot, total);
            return result;
        }

        private static void FinishSnapshotPage(BridgeResult result, BridgeRequest request, BridgeQuery query,
            BridgeQuerySnapshot snapshot, int total)
        {
            int next = query.Offset + query.Limit;
            bool more = next < total;
            result.Add("hasMore", more);
            if (more)
            {
                result.Truncated = true;
                result.ContinuationCursor = BridgeCursor.EncodeSnapshot(request.SessionId, request.Command,
                    query.CursorScope, query.Ordering, snapshot.Id, snapshot.ExpiresUtc.Ticks, next);
            }
            else BridgeQuerySnapshotStore.Remove(snapshot.Id);
        }

        private static BridgeQuery Query(BridgeRequest request, out BridgeResult failure) =>
            BridgeQuery.Parse(request.Argument, request.SessionId, request.Command, out failure);

        private static BridgeResult Paged(string schema, BridgeRequest request, BridgeQuery query, int total) =>
            BridgeResult.Ok(schema).Add("total", total).Add("offset", query.Offset).Add("limit", query.Limit);

        private static void FinishPage(BridgeResult result, BridgeRequest request, BridgeQuery query, int total)
        {
            int next = query.Offset + query.Limit;
            bool more = next < total;
            result.Add("hasMore", more);
            if (more)
            {
                result.Truncated = true;
                result.ContinuationCursor = BridgeCursor.Encode(request.SessionId, request.Command,
                    query.CursorScope, next);
            }
        }

        private static void ApplyScanLimit(BridgeResult result, bool scanTruncated)
        {
            if (!scanTruncated) return;
            result.Status = BridgeStatus.PARTIAL;
            result.Truncated = true;
            result.Warn("scan budget reached; narrow the filter for complete evidence");
        }

        private static bool Matches(string filter, params string[] values) => string.IsNullOrWhiteSpace(filter) ||
            values.Any(value => value?.IndexOf(filter, StringComparison.OrdinalIgnoreCase) >= 0);
        private static bool Contains(string value, string token) => value?.IndexOf(token,
            StringComparison.OrdinalIgnoreCase) >= 0;
        private static string Reference(string session, int mapId, int id) => session + ":" + mapId + ":" + id;
        internal static string Cell(IntVec3 value) => value.x + "," + value.z;
        private static string RectValue(Rect value) => value.x.ToString("0.##") + "," + value.y.ToString("0.##") +
            "," + value.width.ToString("0.##") + "," + value.height.ToString("0.##");
        private static string Target(LocalTargetInfo target) => target.HasThing ? "thing:" + target.Thing.thingIDNumber :
            target.IsValid ? "cell:" + Cell(target.Cell) : "none";
        private static bool SafeFieldType(Type type) => type.IsPrimitive || type.IsEnum || type == typeof(string) ||
            type == typeof(decimal) || type == typeof(IntVec3) || typeof(Def).IsAssignableFrom(type);
        private static string Simple(object value) => value is Def def ? def.defName : BridgeText.Invariant(value);

        internal static string SafeArtifactName(string value, string fallback)
        {
            string candidate = string.IsNullOrWhiteSpace(value) ? fallback : value.Trim();
            if (candidate == null || candidate.Length > 96 || candidate.Any(character =>
                !char.IsLetterOrDigit(character) && character != '-' && character != '_')) return null;
            return candidate;
        }

        private static string SafeSaveName(string value) => SafeArtifactName(value, null);

        private sealed class ComponentRef
        {
            internal string Scope;
            internal int Index;
            internal object Value;
        }
    }

}
