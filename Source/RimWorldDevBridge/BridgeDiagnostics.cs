using System;
using System.Collections;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Linq;
using System.Reflection;
using System.Security.Cryptography;
using System.Text;
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
        private static int lastProjectionMaxItems;
        private static double lastProjectionMaxStepMs;

        internal static int LastProjectionMaxItemsForTests => Volatile.Read(ref lastProjectionMaxItems);
        internal static double LastProjectionMaxStepMsForTests => Volatile.Read(ref lastProjectionMaxStepMs);

        internal static void ResetProjectionMetricsForTests()
        {
            Interlocked.Exchange(ref lastProjectionMaxItems, 0);
            Volatile.Write(ref lastProjectionMaxStepMs, 0d);
        }
        internal delegate void RegisterCommand(string name, string description, BridgeCommandMode mode,
            BridgeCostClass cost, bool requiresMap, string argumentSchema);

        internal static void Register(RegisterCommand register)
        {
            register("PAWNS", "Stable paged pawn summaries.", BridgeCommandMode.PureRead,
                BridgeCostClass.Normal, true, "filter,limit,cursor,fields");
            register("PAWN", "Inspect one session/map-scoped pawn reference.", BridgeCommandMode.PureRead,
                BridgeCostClass.Normal, true, "[session:map:]thingId");
            register("THINGS", "Stable paged thing summaries.", BridgeCommandMode.PureRead,
                BridgeCostClass.Normal, true, "filter,limit,cursor,fields");
            register("THING", "Inspect one session/map-scoped thing reference.", BridgeCommandMode.PureRead,
                BridgeCostClass.Normal, true, "[session:map:]thingId");
            register("DEFS", "Stable paged definition summaries.", BridgeCommandMode.PureRead,
                BridgeCostClass.Normal, false, "kind,filter,limit,cursor,fields");
            register("COMPONENTS", "Stable paged map, game, and world component types.", BridgeCommandMode.PureRead,
                BridgeCostClass.Normal, false, "scope,filter,limit,cursor");
            register("COMPONENT", "Inspect bounded primitive fields on one component.", BridgeCommandMode.PureRead,
                BridgeCostClass.Normal, false, "scope:index|type-filter");
            register("JOBS", "Stable paged active pawn jobs.", BridgeCommandMode.PureRead,
                BridgeCostClass.Normal, true, "filter,limit,cursor");
            register("DESIGNATIONS", "Stable paged map designations.", BridgeCommandMode.PureRead,
                BridgeCostClass.Normal, true, "filter,limit,cursor");
            register("SELECTED", "Inspect current selection without generic reflection.", BridgeCommandMode.PureRead,
                BridgeCostClass.Trivial, false, "none");
            register("UI_STATE", "Current windows, rectangles, resolution, scale, and clipping flags.",
                BridgeCommandMode.PureRead, BridgeCostClass.Normal, false, "limit,cursor");
            register("SELECT", "Select and focus one thing by scoped reference.", BridgeCommandMode.UiOnly,
                BridgeCostClass.Trivial, true, "[session:map:]thingId");
            register("JUMP", "Jump to a bounded current-map cell.", BridgeCommandMode.UiOnly,
                BridgeCostClass.Trivial, true, "x,z");
            register("SCREENSHOT", "Write a full-screen PNG under bridge user data.", BridgeCommandMode.UiOnly,
                BridgeCostClass.Expensive, false, "name");
            register("SCREENSHOT_REGION", "Write a bounded screen region PNG under bridge user data.",
                BridgeCommandMode.UiOnly, BridgeCostClass.Expensive, false,
                "x=<px>&y=<bottom-px>&width=<px>&height=<px>&name=<name>");
            register("REFRESH_CELL", "Mark one map cell mesh dirty for a narrow visual refresh.",
                BridgeCommandMode.Reversible, BridgeCostClass.Trivial, true, "x,z");
            register("LOG_DELTA", "Stable paged RimWorld log messages.", BridgeCommandMode.PureRead,
                BridgeCostClass.Normal, false, "filter,limit,cursor");
            register("DEF_ERRORS", "Definition/configuration errors from the bounded log queue.",
                BridgeCommandMode.PureRead, BridgeCostClass.Normal, false, "filter,limit,cursor");
            register("PATCH_ERRORS", "Harmony/patch errors from the bounded log queue.",
                BridgeCommandMode.PureRead, BridgeCostClass.Normal, false, "filter,limit,cursor");
            register("HARMONY_PATCHES", "Paged Harmony ownership for filtered patched methods.",
                BridgeCommandMode.PureRead, BridgeCostClass.Expensive, false, "filter,limit,cursor");
            register("COMPATIBILITY_REPORT", "Loaded packages, bridge protocol, adapters, and error counts.",
                BridgeCommandMode.PureRead, BridgeCostClass.Normal, false, "none");
            register("CAPTURE_STATE", "Write a bounded semantic game-state capture outside live memory.",
                BridgeCommandMode.PureRead, BridgeCostClass.Expensive, false, "name");
            register("DIFF_STATE", "Compare two stored semantic captures.", BridgeCommandMode.PureRead,
                BridgeCostClass.Expensive, false, "before=<name>&after=<name>&limit=<n>");
            register("EVENTS", "Paged bridge lifecycle and command events.", BridgeCommandMode.PureRead,
                BridgeCostClass.Trivial, false, "filter,limit,cursor");
            register("PERFORMANCE", "On-demand process, GC, scheduler, and bridge-resource metrics.",
                BridgeCommandMode.PureRead, BridgeCostClass.Trivial, false, "none");
            register("BENCHMARK", "Bounded repeated current-map summary benchmark.", BridgeCommandMode.PureRead,
                BridgeCostClass.Expensive, true, "iterations=1..100");
            register("SAVE_GAME", "Save the current game to an explicit development copy.",
                BridgeCommandMode.PersistentMutation, BridgeCostClass.Expensive, false, "name");
            register("LOAD_GAME", "Load an explicit development save and rotate the bridge session.",
                BridgeCommandMode.PotentiallyDestructive, BridgeCostClass.Simulation, false, "name");
        }

        internal static BridgeResult Execute(BridgeExecutionContext context)
        {
            switch (context.Request.Command)
            {
                case "PAWNS": return Pawns(context);
                case "PAWN": return Pawn(context);
                case "THINGS": return Things(context);
                case "THING": return Thing(context);
                case "DEFS": return Defs(context);
                case "COMPONENTS": return Components(context);
                case "COMPONENT": return Component(context);
                case "JOBS": return Jobs(context);
                case "DESIGNATIONS": return Designations(context);
                case "SELECTED": return Selected();
                case "UI_STATE": return UiState(context);
                case "SELECT": return Select(context);
                case "JUMP": return Jump(context);
                case "SCREENSHOT": return Screenshot(context);
                case "SCREENSHOT_REGION": return ScreenshotRegion(context);
                case "REFRESH_CELL": return RefreshCell(context);
                case "LOG_DELTA": return Logs(context, null);
                case "DEF_ERRORS": return Logs(context, "def");
                case "PATCH_ERRORS": return Logs(context, "patch");
                case "HARMONY_PATCHES": return HarmonyPatches(context);
                case "COMPATIBILITY_REPORT": return CompatibilityReport();
                case "CAPTURE_STATE": return CaptureState(context);
                case "DIFF_STATE": return context.Request.PreparedPayload as BridgeResult ??
                    DiffState(context.Request);
                case "EVENTS": return BridgeEventJournal.Report(context.Request);
                case "PERFORMANCE": return Performance();
                case "BENCHMARK": return Benchmark(context);
                case "SAVE_GAME": return SaveGame(context.Request.Argument);
                case "LOAD_GAME": return LoadGame(context.Request.Argument);
                default: return null;
            }
        }

        internal static BridgeResult Prepare(BridgeRequest request)
        {
            if (request?.Command != "DIFF_STATE") return null;
            BridgeResult result = DiffState(request);
            if (!result.IsSuccess) return result;
            request.PreparedPayload = result;
            return null;
        }

        private static BridgeResult Pawns(BridgeExecutionContext context)
        {
            BridgeQuery query = Query(context.Request, out BridgeResult failure);
            if (failure != null) return failure;
            SnapshotProjectionOperation<Pawn> pending = context.Request.CooperativeState as
                SnapshotProjectionOperation<Pawn>;
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
            context.Request.CooperativeState = new SnapshotProjectionOperation<Pawn>(context.Request,
                query, mapId, available,
                index => BridgeGameState.CurrentMap.mapPawns.AllPawnsSpawned[index],
                () => BridgeGameState.CurrentMap?.mapPawns.AllPawnsSpawned.Count ?? 0,
                pawn => pawn.thingIDNumber,
                pawn => string.IsNullOrWhiteSpace(query.Filter) ||
                    Matches(query.Filter, pawn.def?.defName, pawn.LabelShortCap),
                pawn => new BridgeQuerySnapshotRow(pawn.thingIDNumber,
                    PawnLine(pawn, context.SessionId, mapId)), "core.pawns");
            return ((SnapshotProjectionOperation<Pawn>)context.Request.CooperativeState).Step(context);
        }

        private static BridgeResult Pawn(BridgeExecutionContext context)
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

        private static BridgeResult Things(BridgeExecutionContext context)
        {
            BridgeQuery query = Query(context.Request, out BridgeResult failure);
            if (failure != null) return failure;
            SnapshotProjectionOperation<Thing> pending = context.Request.CooperativeState as
                SnapshotProjectionOperation<Thing>;
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
            context.Request.CooperativeState = new SnapshotProjectionOperation<Thing>(context.Request,
                query, mapId, available,
                index => BridgeGameState.CurrentMap.listerThings.AllThings[index],
                () => BridgeGameState.CurrentMap?.listerThings.AllThings.Count ?? 0,
                thing => thing.thingIDNumber,
                thing => string.IsNullOrWhiteSpace(query.Filter) || Matches(query.Filter,
                    thing.def?.defName, thing.LabelShortCap, thing.GetType().FullName),
                thing => new BridgeQuerySnapshotRow(thing.thingIDNumber,
                    ThingLine(thing, context.SessionId, mapId)), "core.things");
            return ((SnapshotProjectionOperation<Thing>)context.Request.CooperativeState).Step(context);
        }

        private static BridgeResult Thing(BridgeExecutionContext context)
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

        private static BridgeResult Defs(BridgeExecutionContext context)
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

        private static BridgeResult Components(BridgeExecutionContext context)
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

        private static BridgeResult Component(BridgeExecutionContext context)
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

        private static BridgeResult Jobs(BridgeExecutionContext context)
        {
            BridgeQuery query = Query(context.Request, out BridgeResult failure);
            if (failure != null) return failure;
            SnapshotProjectionOperation<Pawn> pending = context.Request.CooperativeState as
                SnapshotProjectionOperation<Pawn>;
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
            context.Request.CooperativeState = new SnapshotProjectionOperation<Pawn>(context.Request,
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
            return ((SnapshotProjectionOperation<Pawn>)context.Request.CooperativeState).Step(context);
        }

        private static BridgeResult Designations(BridgeExecutionContext context)
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

        private static BridgeResult Selected()
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

        private static BridgeResult UiState(BridgeExecutionContext context)
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

        private static BridgeResult Select(BridgeExecutionContext context)
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

        private static BridgeResult Jump(BridgeExecutionContext context)
        {
            if (!TryCell(context.Request.Argument, context.Map, out IntVec3 cell, out BridgeResult failure)) return failure;
            Find.CameraDriver.JumpToCurrentMapLoc(cell);
            return BridgeResult.Ok("core.jump").Add("cell", Cell(cell)).WithMutation("camera jump " + Cell(cell));
        }

        private static BridgeResult Screenshot(BridgeExecutionContext context)
        {
            string name = SafeArtifactName(context.Request.Argument, "screenshot-" + DateTime.UtcNow.ToString("yyyyMMdd-HHmmssfff")) + ".png";
            return CaptureScreenshot(new Rect(0f, 0f, Screen.width, Screen.height), Screen.width,
                Screen.height, name, "core.screenshot");
        }

        private static BridgeResult ScreenshotRegion(BridgeExecutionContext context)
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
                    .Add("sha256", Sha256(bytes)).Add("width", width).Add("height", height)
                    .WithMutation("wrote screenshot artifact");
            }
            finally { UnityEngine.Object.Destroy(image); }
        }

        private static BridgeResult RefreshCell(BridgeExecutionContext context)
        {
            if (!TryCell(context.Request.Argument, context.Map, out IntVec3 cell, out BridgeResult failure)) return failure;
            context.Map.mapDrawer.MapMeshDirty(cell, ulong.MaxValue);
            return BridgeResult.Ok("core.refreshCell").Add("cell", Cell(cell))
                .WithMutation("marked map mesh dirty at " + Cell(cell));
        }

        private static BridgeResult Logs(BridgeExecutionContext context, string category)
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

        private static BridgeResult HarmonyPatches(BridgeExecutionContext context)
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

        private static BridgeResult CompatibilityReport()
        {
            int errors = (Log.Messages ?? Enumerable.Empty<LogMessage>()).Count(item => item.type == LogMessageType.Error);
            return BridgeResult.Ok("core.compatibilityReport")
                .Add("bridge", BridgeProtocol.BridgeVersion).Add("protocol", BridgeProtocol.ProtocolVersion)
                .Add("schema", BridgeProtocol.CoreSchema).Add("game", VersionControl.CurrentVersionStringWithoutBuild)
                .Add("loadedMods", LoadedModManager.RunningModsListForReading.Count)
                .Add("adapterIndex", BridgeAdapterCatalog.State).Add("adapterFingerprint", BridgeAdapterCatalog.Fingerprint)
                .Add("logErrors", errors).Add("devMode", Prefs.DevMode)
                .Add("remoteMutationEnabled", RimWorldDevBridgeMod.Settings?.RemoteMutationEnabled ?? true);
        }

        private static BridgeResult CaptureState(BridgeExecutionContext context)
        {
            string name = SafeArtifactName(context.Request.Argument, "capture-" + DateTime.UtcNow.ToString("yyyyMMdd-HHmmssfff"));
            string path = BridgePaths.SafeOutputPath("Captures", name + ".state");
            List<string> lines = BuildCapture(context);
            byte[] bytes = Encoding.UTF8.GetBytes(string.Join("\n", lines));
            File.WriteAllBytes(path, bytes);
            return BridgeResult.Ok("core.captureState").Add("capture", name).Add("path", path)
                .Add("records", lines.Count).Add("bytes", bytes.Length).Add("sha256", Sha256(bytes));
        }

        private static BridgeResult DiffState(BridgeRequest request)
        {
            Dictionary<string, string> options;
            try { options = BridgeProtocol.ParseOptions((request.Argument ?? string.Empty).Replace(';', '&')); }
            catch (Exception exception) { return BridgeResult.Fail(BridgeStatus.INVALID_ARGUMENT, "invalid_diff_options", exception.Message); }
            string beforeName = SafeArtifactName(BridgeProtocol.Value(options, "before"), null);
            string afterName = SafeArtifactName(BridgeProtocol.Value(options, "after"), null);
            if (beforeName == null || afterName == null)
                return BridgeResult.Fail(BridgeStatus.INVALID_ARGUMENT, "capture_names_required");
            string beforePath = BridgePaths.SafeOutputPath("Captures", beforeName + ".state");
            string afterPath = BridgePaths.SafeOutputPath("Captures", afterName + ".state");
            if (!File.Exists(beforePath) || !File.Exists(afterPath))
                return BridgeResult.Fail(BridgeStatus.NOT_FOUND, "capture_not_found")
                    .Add("beforeExists", File.Exists(beforePath)).Add("afterExists", File.Exists(afterPath));
            if (new FileInfo(beforePath).Length > 8 * 1024 * 1024 || new FileInfo(afterPath).Length > 8 * 1024 * 1024)
                return BridgeResult.Fail(BridgeStatus.INVALID_ARGUMENT, "capture_too_large");
            Dictionary<string, string> before = CaptureDictionary(beforePath);
            Dictionary<string, string> after = CaptureDictionary(afterPath);
            List<string> keys = before.Keys.Union(after.Keys).OrderBy(value => value, StringComparer.Ordinal).ToList();
            int limit = BridgeProtocol.ParseBoundedInt(BridgeProtocol.Value(options, "limit"), 100, 1, 500);
            List<string> changes = new List<string>();
            foreach (string key in keys)
            {
                before.TryGetValue(key, out string oldValue);
                after.TryGetValue(key, out string newValue);
                if (oldValue == newValue) continue;
                changes.Add((oldValue == null ? "added" : newValue == null ? "removed" : "changed") +
                    "=key:" + BridgeText.Clean(key) + " before:" + BridgeText.Clean(oldValue) +
                    " after:" + BridgeText.Clean(newValue));
            }
            BridgeResult result = BridgeResult.Ok("core.stateDiff").Add("before", beforeName).Add("after", afterName)
                .Add("changes", changes.Count);
            foreach (string change in changes.Take(limit)) result.AddLine(change);
            if (changes.Count > limit) { result.Status = BridgeStatus.PARTIAL; result.Truncated = true; }
            return result;
        }

        private static BridgeResult Performance()
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

        private static BridgeResult Benchmark(BridgeExecutionContext context)
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

        private static BridgeResult SaveGame(string argument)
        {
            string name = SafeSaveName(argument);
            if (name == null) return BridgeResult.Fail(BridgeStatus.INVALID_ARGUMENT, "invalid_save_name");
            if (Current.Game == null) return BridgeResult.Fail(BridgeStatus.UNAVAILABLE, "game_required");
            GameDataSaveLoader.SaveGame(name);
            return BridgeResult.Ok("core.saveGame").Add("save", name).WithMutation("saved development copy " + name);
        }

        private static BridgeResult LoadGame(string argument)
        {
            string name = SafeSaveName(argument);
            if (name == null) return BridgeResult.Fail(BridgeStatus.INVALID_ARGUMENT, "invalid_save_name");
            string path = GenFilePaths.FilePathForSavedGame(name);
            if (!File.Exists(path)) return BridgeResult.Fail(BridgeStatus.NOT_FOUND, "save_not_found").Add("save", name);
            GameDataSaveLoader.CheckVersionAndLoadGame(name);
            return BridgeResult.Ok("core.loadGame").Add("save", name).WithMutation("requested load of " + name);
        }

        private static List<string> BuildCapture(BridgeExecutionContext context)
        {
            List<string> lines = new List<string>
            {
                "meta/session=" + context.SessionId,
                "meta/tick=" + context.Tick,
                "meta/gameVersion=" + VersionControl.CurrentVersionStringWithoutBuild,
                "meta/adapterFingerprint=" + BridgeAdapterCatalog.Fingerprint,
                "meta/mapCount=" + (BridgeGameState.Maps?.Count ?? 0)
            };
            foreach (Map map in (BridgeGameState.Maps ?? new List<Map>()).OrderBy(value => value.uniqueID))
            {
                context.ThrowIfCancellationRequested();
                lines.Add("map/" + map.uniqueID + "/tile=" + map.Tile);
                lines.Add("map/" + map.uniqueID + "/things=" + map.listerThings.AllThings.Count);
                lines.Add("map/" + map.uniqueID + "/pawns=" + map.mapPawns.AllPawnsSpawned.Count);
                foreach (Thing thing in map.listerThings.AllThings.OrderBy(value => value.thingIDNumber).Take(5000))
                    lines.Add("thing/" + map.uniqueID + "/" + thing.thingIDNumber + "=" +
                        BridgeText.Clean(thing.def?.defName) + "|" + (thing.Spawned ? Cell(thing.Position) : "unspawned") +
                        "|stack:" + thing.stackCount + "|hp:" + (thing.def?.useHitPoints == true ? thing.HitPoints : -1));
            }
            foreach (LogMessage message in (Log.Messages ?? Enumerable.Empty<LogMessage>()).Where(item =>
                item.type == LogMessageType.Error || item.type == LogMessageType.Warning).Take(200))
                lines.Add("log/" + lines.Count + "=" + message.type + "|" + BridgeText.Clean(message.text));
            return lines.OrderBy(value => value, StringComparer.Ordinal).ToList();
        }

        private static Dictionary<string, string> CaptureDictionary(string path)
        {
            Dictionary<string, string> result = new Dictionary<string, string>(StringComparer.Ordinal);
            foreach (string line in File.ReadAllLines(path))
            {
                int split = line.IndexOf('=');
                if (split > 0) result[line.Substring(0, split)] = line.Substring(split + 1);
            }
            return result;
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

        private static BridgeResult SnapshotTimeLimit()
        {
            BridgeResult result = BridgeResult.Fail(BridgeStatus.PARTIAL, "snapshot_time_limit",
                "The query snapshot exceeded the effective main-thread budget; narrow the filter.");
            result.Truncated = true;
            return result.Warn("query snapshot was not created within the main-thread budget");
        }

        private static BridgeResult SnapshotMemoryLimit()
        {
            BridgeResult result = BridgeResult.Fail(BridgeStatus.BUSY, "snapshot_memory_limit",
                "The query snapshot exceeded the configured memory bound; narrow the filter.");
            result.Truncated = true;
            return result.Warn("query snapshot was not created within the memory bound");
        }

        private static void AddSnapshotRow(List<BridgeQuerySnapshotRow> rows, BridgeQuerySnapshotRow row,
            ref long estimatedBytes)
        {
            estimatedBytes += row.EstimatedBytes;
            if (estimatedBytes > BridgeQuerySnapshotStore.AvailableBytes)
                throw new SnapshotMemoryExceededException();
            rows.Add(row);
        }

        private static void CheckSnapshotBudget(long start)
        {
            int budgetMs = BridgeRuntime.EffectiveMainThreadBudgetMs;
            if (budgetMs > 0 && BridgeTiming.Milliseconds(start) > budgetMs)
                throw new SnapshotBudgetExceededException();
        }

        private sealed class SnapshotProjectionOperation<T>
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

            internal SnapshotProjectionOperation(BridgeRequest request, BridgeQuery query, int mapId,
                int sourceCount, Func<int, T> sourceAt, Func<int> currentCount, Func<T, int> stableId,
                Func<T, bool> matches,
                Func<T, BridgeQuerySnapshotRow> project, string schema)
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
                        try
                        {
                            value = sourceAt(index++);
                        }
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
                            if (row != null) AddSnapshotRow(rows, row, ref estimatedBytes);
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

                    CheckSnapshotBudget(stepStart);
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
                        return SnapshotTimeLimit();
                    }
                    query.SnapshotId = snapshot.Id;
                    query.SnapshotExpiryTicks = snapshot.ExpiresUtc.Ticks;
                    request.CooperativeState = null;
                    return SnapshotPage(schema, request, query, snapshot);
                }
                catch (SnapshotMemoryExceededException)
                {
                    request.CooperativeState = null;
                    return SnapshotMemoryLimit();
                }
                catch (SnapshotBudgetExceededException)
                {
                    request.CooperativeState = null;
                    return SnapshotTimeLimit();
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
                        previousItems = Volatile.Read(ref lastProjectionMaxItems);
                        if (processed <= previousItems) break;
                    }
                    while (Interlocked.CompareExchange(ref lastProjectionMaxItems, processed,
                        previousItems) != previousItems);

                    double elapsed = BridgeTiming.Milliseconds(stepStart);
                    double previousStep;
                    do
                    {
                        previousStep = Volatile.Read(ref lastProjectionMaxStepMs);
                        if (elapsed <= previousStep) break;
                    }
                    while (Interlocked.CompareExchange(ref lastProjectionMaxStepMs, elapsed,
                        previousStep) != previousStep);
                }
            }

            private bool StillValid(BridgeExecutionContext context)
            {
                if (!string.Equals(request.SessionId, BridgeRuntime.SessionId, StringComparison.Ordinal))
                    return false;
                if (context.Map == null || context.Map.uniqueID != mapId)
                    return false;
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

        private sealed class SnapshotBudgetExceededException : Exception { }
        private sealed class SnapshotMemoryExceededException : Exception { }

        private static BridgeResult SnapshotPage(string schema, BridgeRequest request, BridgeQuery query,
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
        private static string Cell(IntVec3 value) => value.x + "," + value.z;
        private static string RectValue(Rect value) => value.x.ToString("0.##") + "," + value.y.ToString("0.##") +
            "," + value.width.ToString("0.##") + "," + value.height.ToString("0.##");
        private static string Target(LocalTargetInfo target) => target.HasThing ? "thing:" + target.Thing.thingIDNumber :
            target.IsValid ? "cell:" + Cell(target.Cell) : "none";
        private static bool SafeFieldType(Type type) => type.IsPrimitive || type.IsEnum || type == typeof(string) ||
            type == typeof(decimal) || type == typeof(IntVec3) || typeof(Def).IsAssignableFrom(type);
        private static string Simple(object value) => value is Def def ? def.defName : BridgeText.Invariant(value);

        private static string SafeArtifactName(string value, string fallback)
        {
            string candidate = string.IsNullOrWhiteSpace(value) ? fallback : value.Trim();
            if (candidate == null || candidate.Length > 96 || candidate.Any(character =>
                !char.IsLetterOrDigit(character) && character != '-' && character != '_')) return null;
            return candidate;
        }

        private static string SafeSaveName(string value) => SafeArtifactName(value, null);

        private static string Sha256(byte[] bytes)
        {
            using (SHA256 algorithm = SHA256.Create())
                return string.Concat(algorithm.ComputeHash(bytes).Select(value => value.ToString("x2")));
        }

        private sealed class ComponentRef
        {
            internal string Scope;
            internal int Index;
            internal object Value;
        }
    }

    internal static class BridgeEventJournal
    {
        private const int Limit = 512;
        private static readonly object Gate = new object();
        private static readonly Queue<EventRecord> Values = new Queue<EventRecord>();
        private static long sequence;

        internal static void Record(string kind, string detail)
        {
            lock (Gate)
            {
                Values.Enqueue(new EventRecord
                {
                    Sequence = ++sequence,
                    Utc = DateTime.UtcNow,
                    Kind = BridgeText.Clean(kind),
                    Detail = BridgeText.Clean(detail)
                });
                while (Values.Count > Limit) Values.Dequeue();
            }
        }

        internal static BridgeResult Report(BridgeRequest request)
        {
            BridgeQuery query = BridgeQuery.Parse(request.Argument, request.SessionId, request.Command,
                out BridgeResult failure);
            if (failure != null) return failure;
            List<EventRecord> values;
            lock (Gate) values = Values.Where(item => string.IsNullOrEmpty(query.Filter) ||
                item.Kind.IndexOf(query.Filter, StringComparison.OrdinalIgnoreCase) >= 0 ||
                item.Detail.IndexOf(query.Filter, StringComparison.OrdinalIgnoreCase) >= 0).ToList();
            BridgeResult result = BridgeResult.Ok("core.events").Add("total", values.Count)
                .Add("offset", query.Offset).Add("limit", query.Limit);
            foreach (EventRecord item in values.Skip(query.Offset).Take(query.Limit))
                result.AddLine("event=seq:" + item.Sequence + " utc:" + item.Utc.ToString("o") +
                    " kind:" + item.Kind + " detail:" + item.Detail);
            int next = query.Offset + query.Limit;
            result.Add("hasMore", next < values.Count);
            if (next < values.Count)
            {
                result.Truncated = true;
                result.ContinuationCursor = BridgeCursor.Encode(request.SessionId, request.Command,
                    query.CursorScope, next);
            }
            return result;
        }

        private sealed class EventRecord
        {
            internal long Sequence;
            internal DateTime Utc;
            internal string Kind;
            internal string Detail;
        }
    }
}
