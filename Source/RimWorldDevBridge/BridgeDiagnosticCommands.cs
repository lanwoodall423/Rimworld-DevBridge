namespace RimWorldDevBridge
{
    // Keeps the stable command table and dispatch contract separate from query implementations.
    internal static class BridgeDiagnosticCommands
    {
        internal static void Register(BridgeDiagnostics.RegisterCommand register)
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
            register("PATCH_ERRORS", "Harmony/patch errors from the bounded log queue.", BridgeCommandMode.PureRead,
                BridgeCostClass.Normal, false, "filter,limit,cursor");
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
                case "PAWNS": return BridgeDiagnostics.Pawns(context);
                case "PAWN": return BridgeDiagnostics.Pawn(context);
                case "THINGS": return BridgeDiagnostics.Things(context);
                case "THING": return BridgeDiagnostics.Thing(context);
                case "DEFS": return BridgeDiagnostics.Defs(context);
                case "COMPONENTS": return BridgeDiagnostics.Components(context);
                case "COMPONENT": return BridgeDiagnostics.Component(context);
                case "JOBS": return BridgeDiagnostics.Jobs(context);
                case "DESIGNATIONS": return BridgeDiagnostics.Designations(context);
                case "SELECTED": return BridgeDiagnostics.Selected();
                case "UI_STATE": return BridgeDiagnostics.UiState(context);
                case "SELECT": return BridgeDiagnostics.Select(context);
                case "JUMP": return BridgeDiagnostics.Jump(context);
                case "SCREENSHOT": return BridgeDiagnostics.Screenshot(context);
                case "SCREENSHOT_REGION": return BridgeDiagnostics.ScreenshotRegion(context);
                case "REFRESH_CELL": return BridgeDiagnostics.RefreshCell(context);
                case "LOG_DELTA": return BridgeDiagnostics.Logs(context, null);
                case "DEF_ERRORS": return BridgeDiagnostics.Logs(context, "def");
                case "PATCH_ERRORS": return BridgeDiagnostics.Logs(context, "patch");
                case "HARMONY_PATCHES": return BridgeDiagnostics.HarmonyPatches(context);
                case "COMPATIBILITY_REPORT": return BridgeDiagnostics.CompatibilityReport();
                case "CAPTURE_STATE": return BridgeDiagnostics.CaptureState(context);
                case "DIFF_STATE": return context.Request.PreparedPayload as BridgeResult ??
                    BridgeDiagnostics.DiffState(context.Request);
                case "EVENTS": return BridgeEventJournal.Report(context.Request);
                case "PERFORMANCE": return BridgeDiagnostics.Performance();
                case "BENCHMARK": return BridgeDiagnostics.Benchmark(context);
                case "SAVE_GAME": return BridgeDiagnostics.SaveGame(context.Request.Argument);
                case "LOAD_GAME": return BridgeDiagnostics.LoadGame(context.Request.Argument);
                default: return null;
            }
        }

        internal static BridgeResult Prepare(BridgeRequest request)
        {
            if (request?.Command != "DIFF_STATE") return null;
            BridgeResult result = BridgeDiagnostics.DiffState(request);
            if (!result.IsSuccess) return result;
            request.PreparedPayload = result;
            return null;
        }
    }
}
