using System;
using System.Collections.Generic;
using System.Linq;
using RimWorld;
using Verse;

namespace RimWorldDevBridge
{
    internal static class BridgeCommands
    {
        private static readonly Dictionary<string, BridgeCommandDescriptor> Commands =
            new Dictionary<string, BridgeCommandDescriptor>(StringComparer.OrdinalIgnoreCase);

        static BridgeCommands()
        {
            Register("PING", "Confirm the active bridge session.", BridgeCommandMode.PureRead,
                BridgeCostClass.Trivial, false);
            Register("SYNC", "Compare bridge and adapter-manifest fingerprints.", BridgeCommandMode.PureRead,
                BridgeCostClass.Trivial, false);
            Register("STATUS", "Compact bridge, scheduler, and game status.", BridgeCommandMode.PureRead,
                BridgeCostClass.Trivial, false);
            Register("SESSION", "Current game session identity and write context.", BridgeCommandMode.PureRead,
                BridgeCostClass.Trivial, false);
            Register("CAPABILITIES", "List typed core and available adapter commands.", BridgeCommandMode.PureRead,
                BridgeCostClass.Normal, false);
            Register("HELP", "Describe commands by adapter or read/write mode.", BridgeCommandMode.PureRead,
                BridgeCostClass.Normal, false, "filter:string");
            Register("DESCRIBE", "Describe one command contract.", BridgeCommandMode.PureRead,
                BridgeCostClass.Trivial, false, "command:string");
            Register("WRITE_LEASE", "Acquire a short-lived write lease for sandbox or confirmed live use.",
                BridgeCommandMode.PureRead, BridgeCostClass.Trivial, false, "context:sandbox|live-confirmed");
            Register("RENEW_WRITE_LEASE", "Extend an active write lease without changing its context.",
                BridgeCommandMode.PureRead, BridgeCostClass.Trivial, false, "lease:string");
            Register("REVOKE_WRITE_LEASE", "Revoke an active write lease immediately.",
                BridgeCommandMode.PureRead, BridgeCostClass.Trivial, false, "lease:string");
            Register("SET_SPEED", "Set game speed from 0 through 4.", BridgeCommandMode.Reversible,
                BridgeCostClass.Trivial, true, "speed:int[0,4]");
            Register("SCHEDULER_METRICS", "Report queue and main-thread timing metrics.",
                BridgeCommandMode.PureRead, BridgeCostClass.Trivial, false);
            Register("COMMAND_METRICS", "Report bounded core and adapter command timings.",
                BridgeCommandMode.PureRead, BridgeCostClass.Trivial, false);
            Register("ADAPTER_HEALTH", "Report available, loaded, superseded, failed, and quarantined adapters.",
                BridgeCommandMode.PureRead, BridgeCostClass.Trivial, false);
            Register("HOT_ADAPTER_STATUS", "Compatibility alias for adapter health.",
                BridgeCommandMode.PureRead, BridgeCostClass.Trivial, false);
            Register("RELOAD_HOT_ADAPTERS", "Reindex manifests and switch to newer compatible generations.",
                BridgeCommandMode.PureRead, BridgeCostClass.Normal, false);
            Register("RELOAD_ADAPTERS", "Compatibility alias for manifest reindexing.",
                BridgeCommandMode.PureRead, BridgeCostClass.Normal, false);
            Register("RELOAD_BRIDGE", "Reindex adapter manifests and reload declarative macros.",
                BridgeCommandMode.PureRead, BridgeCostClass.Normal, false);
            Register("MODS", "Loaded mod identities in load order.", BridgeCommandMode.PureRead,
                BridgeCostClass.Normal, false, "limit,cursor,filter");
            Register("DLC", "Active official expansions.", BridgeCommandMode.PureRead,
                BridgeCostClass.Trivial, false);
            Register("MAPS", "Loaded maps with stable IDs.", BridgeCommandMode.PureRead,
                BridgeCostClass.Trivial, false);
            Register("SNAPSHOT", "Compact current-map summary.", BridgeCommandMode.PureRead,
                BridgeCostClass.Normal, true);
            Register("MAP_SUMMARY", "Compact current-map summary.", BridgeCommandMode.PureRead,
                BridgeCostClass.Normal, true);
            Register("SAVE_INFO", "Current game and save context.", BridgeCommandMode.PureRead,
                BridgeCostClass.Trivial, false);
            Register("SETTINGS", "Bridge safety and scheduler settings.", BridgeCommandMode.PureRead,
                BridgeCostClass.Trivial, false);
            Register("RESEARCH", "Current research and completed project count.", BridgeCommandMode.PureRead,
                BridgeCostClass.Normal, false);
            BridgeDiagnostics.Register(Register);
            Commands["PAWNS"].Cooperative = true;
            Commands["THINGS"].Cooperative = true;
            Commands["JOBS"].Cooperative = true;
        }

        internal static IEnumerable<BridgeCommandDescriptor> All => Commands.Values.OrderBy(value => value.Name);

        internal static BridgeCommandDescriptor Describe(string command)
        {
            Commands.TryGetValue(BridgeText.NormalizeCommand(command), out BridgeCommandDescriptor descriptor);
            return descriptor;
        }

        internal static BridgeResult Execute(BridgeExecutionContext context)
        {
            string command = context.Request.Command;
            switch (command)
            {
                case "PING": return BridgeResult.Ok("core.ping").Add("pong", true).Add("tick", context.Tick);
                case "SYNC": return Sync(context.Request.Argument);
                case "STATUS": return Status();
                case "SESSION": return Session();
                case "CAPABILITIES": return Capabilities();
                case "HELP": return Help(context.Request.Argument);
                case "DESCRIBE": return DescribeResult(context.Request.Argument);
                case "WRITE_LEASE": return BridgeRuntime.AcquireWriteLease(context.Request.Argument);
                case "RENEW_WRITE_LEASE": return BridgeRuntime.RenewWriteLease(LeaseToken(context.Request));
                case "REVOKE_WRITE_LEASE": return BridgeRuntime.RevokeWriteLease(LeaseToken(context.Request));
                case "SET_SPEED": return SetSpeed(context.Request.Argument);
                case "SCHEDULER_METRICS": return BridgeRuntime.SchedulerMetrics();
                case "COMMAND_METRICS": return BridgeMetrics.Report();
                case "ADAPTER_HEALTH":
                case "HOT_ADAPTER_STATUS": return BridgeAdapterCatalog.Health();
                case "RELOAD_HOT_ADAPTERS":
                case "RELOAD_ADAPTERS": return BridgeAdapterCatalog.Reindex();
                case "RELOAD_BRIDGE":
                    BridgeOrchestration.Reload();
                    return BridgeAdapterCatalog.Reindex().Add("macros", "reloaded");
                case "MODS": return Mods(context.Request);
                case "DLC": return Dlc();
                case "MAPS": return Maps();
                case "SNAPSHOT":
                case "MAP_SUMMARY": return MapSummary(context.Map);
                case "SAVE_INFO": return SaveInfo();
                case "SETTINGS": return Settings();
                case "RESEARCH": return Research();
                default: return BridgeDiagnostics.Execute(context);
            }
        }

        internal static BridgeResult Prepare(BridgeRequest request)
        {
            return BridgeDiagnostics.Prepare(request);
        }

        private static string LeaseToken(BridgeRequest request)
        {
            return string.IsNullOrWhiteSpace(request.AuthToken) ? request.Argument : request.AuthToken;
        }

        private static void Register(string name, string description, BridgeCommandMode mode,
            BridgeCostClass cost, bool requiresMap, string argumentSchema = "none")
        {
            Commands[name] = new BridgeCommandDescriptor
            {
                Name = name,
                Description = description,
                Provider = "core",
                ProviderVersion = BridgeProtocol.BridgeVersion,
                Mode = mode,
                Cost = cost,
                RequiresMap = requiresMap,
                ArgumentSchema = argumentSchema,
                ResultSchema = "core." + name.ToLowerInvariant(),
                SchemaVersion = 1
            };
        }

        private static BridgeResult Status()
        {
            BridgeRuntime.BridgeRuntimeStateSnapshot state = BridgeRuntime.StateSnapshot;
            BridgeResult result = BridgeResult.Ok("core.status")
                .Add("bridgeVersion", BridgeProtocol.BridgeVersion)
                .Add("protocolVersion", BridgeProtocol.ProtocolVersion)
                .Add("state", state.TransportActive ? (state.TransportReady ? "ON" : "ACTIVATING") : "DORMANT")
                .Add("transportGeneration", state.TransportGeneration)
                .Add("transportReady", state.TransportReady)
                .Add("gameVersion", VersionControl.CurrentVersionStringWithoutBuild)
                .Add("devMode", Prefs.DevMode)
                .Add("map", BridgeGameState.CurrentMap?.uniqueID.ToString() ?? "none")
                .Add("tick", BridgeGameState.TickManager?.TicksGame ?? -1)
                .Add("clients", state.ConnectedClients)
                .Add("clientLimit", state.ConnectedClientLimit)
                .Add("adapterIndex", BridgeAdapterCatalog.State)
                .Add("bootstrapMs", BridgeRuntime.BootstrapMs)
                .Add("harmonyMs", BridgeRuntime.HarmonyMs)
                .Add("finalizeInitMs", BridgeRuntime.FinalizeInitMs)
                .Add("activationMs", BridgeRuntime.ActivationMs)
                .Add("bootstrapManagedDeltaBytesApprox", BridgeRuntime.BootstrapManagedDeltaBytes);
            return BridgeRuntime.AddSessionContext(result, state);
        }

        private static BridgeResult Session()
        {
            BridgeRuntime.BridgeRuntimeStateSnapshot state = BridgeRuntime.StateSnapshot;
            BridgeResult result = BridgeResult.Ok("core.session")
                .Add("gameLoaded", Current.Game != null)
                .Add("mapLoaded", BridgeGameState.CurrentMap != null)
                .Add("remoteMutationEnabled", RimWorldDevBridgeMod.Settings?.RemoteMutationEnabled ?? true);
            return BridgeRuntime.AddSessionContext(result, state);
        }

        private static BridgeResult Sync(string known)
        {
            BridgeRuntime.BridgeRuntimeStateSnapshot state = BridgeRuntime.StateSnapshot;
            string fingerprint = BridgeAdapterCatalog.Fingerprint;
            bool same = string.Equals((known ?? string.Empty).Trim(), fingerprint,
                StringComparison.OrdinalIgnoreCase);
            BridgeResult result = BridgeResult.Ok("core.sync")
                .Add("sync", same ? "same" : "changed")
                .Add("fingerprint", fingerprint)
                .Add("bridge", BridgeProtocol.BridgeVersion)
                .Add("protocol", BridgeProtocol.ProtocolVersion)
                .Add("restart", false)
                .Add("adapterIndex", BridgeAdapterCatalog.State);
            return BridgeRuntime.AddSessionContext(result, state);
        }

        private static BridgeResult Capabilities()
        {
            BridgeResult result = BridgeResult.Ok("core.capabilities")
                .Add("bridge", BridgeProtocol.BridgeVersion)
                .Add("protocol", BridgeProtocol.ProtocolVersion)
                .Add("formats", "line,json")
                .Add("requestBytes", BridgeProtocol.MaxRequestBytes)
                .Add("responseBytes", BridgeProtocol.MaxResponseBytes)
                .Add("pagination", true)
                .Add("writeLeases", true)
                .Add("idempotency", true);
            foreach (BridgeCommandDescriptor descriptor in All.Concat(BridgeAdapterCatalog.Commands)
                .Concat(BridgeFeatureTests.Commands)
                .Concat(BridgeOrchestration.Commands)
                .OrderBy(value => value.Name))
                result.AddLine(CommandLine(descriptor));
            return result;
        }

        private static BridgeResult Help(string filter)
        {
            string value = (filter ?? string.Empty).Trim();
            IEnumerable<BridgeCommandDescriptor> commands = All.Concat(BridgeAdapterCatalog.Commands)
                .Concat(BridgeFeatureTests.Commands)
                .Concat(BridgeOrchestration.Commands);
            if (value.Equals("read", StringComparison.OrdinalIgnoreCase))
                commands = commands.Where(item => item.Mode == BridgeCommandMode.PureRead);
            else if (value.Equals("write", StringComparison.OrdinalIgnoreCase))
                commands = commands.Where(item => item.Mode != BridgeCommandMode.PureRead);
            else if (value.Equals("available", StringComparison.OrdinalIgnoreCase))
                commands = commands.Where(item => item != null);
            else if (!string.IsNullOrEmpty(value))
                commands = commands.Where(item => item.Provider.IndexOf(value, StringComparison.OrdinalIgnoreCase) >= 0 ||
                    item.Name.IndexOf(value, StringComparison.OrdinalIgnoreCase) >= 0);
            BridgeResult result = BridgeResult.Ok("core.help")
                .Add("request", "id|COMMAND|argument|timeoutMs=4500&session=<id>&format=line|json")
                .Add("write", "WRITE_LEASE sandbox, then lease=<token>&idempotency=<stable-key>");
            foreach (BridgeCommandDescriptor descriptor in commands.OrderBy(item => item.Name))
                result.AddLine(CommandLine(descriptor));
            if (result.Lines.Count == 0) result.Status = BridgeStatus.NOT_FOUND;
            return result;
        }

        private static BridgeResult DescribeResult(string command)
        {
            BridgeRequest request = new BridgeRequest
            {
                Command = BridgeText.NormalizeCommand(command),
                Argument = string.Empty,
                SessionId = BridgeRuntime.SessionId,
                EnqueuedUtc = DateTime.UtcNow,
                DeadlineUtc = DateTime.UtcNow.AddSeconds(5),
                AllowExpensive = true
            };
            BridgeCommandDescriptor descriptor = BridgeDispatch.Describe(request);
            if (descriptor == null) return BridgeResult.Fail(BridgeStatus.NOT_FOUND, "unknown_command");
            return BridgeResult.Ok("core.commandDescription")
                .Add("name", descriptor.Name)
                .Add("description", descriptor.Description)
                .Add("provider", descriptor.Provider)
                .Add("providerVersion", descriptor.ProviderVersion)
                .Add("mode", descriptor.Mode)
                .Add("cost", descriptor.Cost)
                .Add("requiresMap", descriptor.RequiresMap)
                .Add("argumentSchema", descriptor.ArgumentSchema)
                .Add("resultSchema", descriptor.ResultSchema)
                .Add("schemaVersion", descriptor.SchemaVersion)
                .Add("executionContract", descriptor.Cooperative ? "cooperative-v1" :
                    descriptor.NonCooperative ? "legacy-sync-non-cooperative" : "sync");
        }

        private static BridgeResult SetSpeed(string argument)
        {
            if (!int.TryParse(argument, out int speed) || speed < 0 || speed > 4)
                return BridgeResult.Fail(BridgeStatus.INVALID_ARGUMENT, "invalid_speed", "Expected 0 through 4.");
            TimeSpeed before = Find.TickManager.CurTimeSpeed;
            Find.TickManager.CurTimeSpeed = (TimeSpeed)speed;
            return BridgeResult.Ok("core.setSpeed")
                .Add("before", (int)before)
                .Add("after", speed)
                .Add("changed", (int)before != speed)
                .WithMutation("timeSpeed " + (int)before + " -> " + speed);
        }

        private static BridgeResult Mods(BridgeRequest request)
        {
            BridgeQuery query = BridgeQuery.Parse(request.Argument, request.SessionId, request.Command,
                out BridgeResult failure);
            if (failure != null) return failure;
            List<ModContentPack> source = LoadedModManager.RunningModsListForReading
                .Where(mod => string.IsNullOrEmpty(query.Filter) ||
                    (mod.Name ?? string.Empty).IndexOf(query.Filter, StringComparison.OrdinalIgnoreCase) >= 0 ||
                    (mod.PackageIdPlayerFacing ?? string.Empty).IndexOf(query.Filter, StringComparison.OrdinalIgnoreCase) >= 0)
                .ToList();
            BridgeResult result = BridgeResult.Ok("core.mods").Add("total", source.Count)
                .Add("offset", query.Offset).Add("limit", query.Limit);
            foreach (var pair in source.Select((mod, index) => new { mod, index }).Skip(query.Offset).Take(query.Limit))
                result.AddLine("mod=index:" + pair.index + " packageId:" +
                    BridgeText.Clean(pair.mod.PackageIdPlayerFacing) + " name:" + BridgeText.Clean(pair.mod.Name) +
                    " version:" + BridgeText.Clean(pair.mod.ModMetaData?.ModVersion));
            ApplyPage(result, request, query, source.Count);
            return result;
        }

        private static BridgeResult Dlc()
        {
            BridgeResult result = BridgeResult.Ok("core.dlc");
            foreach (ModContentPack mod in LoadedModManager.RunningModsListForReading.Where(item => item.IsCoreMod ||
                (item.PackageIdPlayerFacing ?? string.Empty).StartsWith("ludeon.rimworld", StringComparison.OrdinalIgnoreCase)))
                result.AddLine("dlc=packageId:" + BridgeText.Clean(mod.PackageIdPlayerFacing) +
                    " name:" + BridgeText.Clean(mod.Name));
            return result.Add("count", result.Lines.Count);
        }

        private static BridgeResult Maps()
        {
            BridgeResult result = BridgeResult.Ok("core.maps");
            foreach (Map map in (BridgeGameState.Maps ?? new List<Map>()).OrderBy(item => item.uniqueID))
                result.AddLine("map=id:" + map.uniqueID + " tile:" + map.Tile + " size:" + map.Size.x +
                    "x" + map.Size.z + " current:" + (map == BridgeGameState.CurrentMap));
            return result.Add("count", result.Lines.Count);
        }

        private static BridgeResult MapSummary(Map map)
        {
            if (map == null) return BridgeResult.Fail(BridgeStatus.UNAVAILABLE, "map_required");
            IReadOnlyList<Pawn> pawns = map.mapPawns.AllPawnsSpawned;
            return BridgeResult.Ok("core.mapSummary")
                .Add("mapId", map.uniqueID).Add("tile", map.Tile).Add("width", map.Size.x).Add("height", map.Size.z)
                .Add("tick", BridgeGameState.TickManager?.TicksGame ?? -1).Add("speed", BridgeGameState.TickManager?.CurTimeSpeed.ToString() ?? "none")
                .Add("pawns", pawns.Count).Add("colonists", map.mapPawns.FreeColonistsSpawned.Count)
                .Add("animals", pawns.Count(pawn => pawn.RaceProps?.Animal == true))
                .Add("hostile", pawns.Count(pawn => pawn.HostileTo(Faction.OfPlayer)))
                .Add("things", map.listerThings.AllThings.Count).Add("components", map.components.Count)
                .Add("selected", Find.Selector?.SingleSelectedThing?.thingIDNumber.ToString() ?? "none")
                .Add("windows", Find.WindowStack?.Windows?.Count ?? 0);
        }

        private static BridgeResult SaveInfo()
        {
            BridgeRuntime.BridgeRuntimeStateSnapshot state = BridgeRuntime.StateSnapshot;
            BridgeResult result = BridgeResult.Ok("core.saveInfo")
                .Add("gameLoaded", Current.Game != null)
                .Add("programState", Current.ProgramState)
                .Add("worldSeed", BridgeGameState.World?.info?.seedString ?? "none")
                .Add("maps", BridgeGameState.Maps?.Count ?? 0);
            return BridgeRuntime.AddSessionContext(result, state);
        }

        private static BridgeResult Settings()
        {
            BridgeSettings settings = RimWorldDevBridgeMod.Settings ?? new BridgeSettings();
            return BridgeResult.Ok("core.settings")
                .Add("remoteMutationEnabled", settings.RemoteMutationEnabled)
                .Add("queueCapacity", settings.QueueCapacity)
                .Add("queueCapacityEffective", BridgeRuntime.EffectiveQueueCapacity)
                .Add("queueCapacityPending", BridgeRuntime.QueueCapacityPending)
                .Add("connectedClientLimit", settings.ConnectedClientLimit)
                .Add("mainThreadBudgetMs", settings.MainThreadBudgetMs)
                .Add("mainThreadBudgetMsEffective", BridgeRuntime.EffectiveMainThreadBudgetMs)
                .Add("mainThreadBudgetPending", BridgeRuntime.MainThreadBudgetPending)
                .Add("schedulerReconfiguration", "immediate_on_apply")
                .Add("retainedAdapterRestartThreshold", settings.RetainedAdapterRestartThreshold);
        }

        private static BridgeResult Research()
        {
            ResearchManager manager = Current.Game == null ? null : Find.ResearchManager;
            if (manager == null) return BridgeResult.Fail(BridgeStatus.UNAVAILABLE, "game_required");
            ResearchProjectDef current = manager.GetProject();
            int finished = DefDatabase<ResearchProjectDef>.AllDefsListForReading.Count(def => def.IsFinished);
            return BridgeResult.Ok("core.research")
                .Add("current", current?.defName ?? "none")
                .Add("progress", current == null ? 0f : manager.GetProgress(current))
                .Add("finished", finished)
                .Add("total", DefDatabase<ResearchProjectDef>.DefCount);
        }

        private static string CommandLine(BridgeCommandDescriptor descriptor) =>
            "cmd=" + descriptor.Name + " mode:" + descriptor.Mode + " cost:" + descriptor.Cost +
            " adapter:" + BridgeText.Clean(descriptor.Provider) + " version:" +
            BridgeText.Clean(descriptor.ProviderVersion) + " contract:" +
            (descriptor.Cooperative ? "cooperative-v1" : descriptor.NonCooperative ?
                "legacy-sync-non-cooperative" : "sync") + " desc:" + BridgeText.Clean(descriptor.Description);

        private static void ApplyPage(BridgeResult result, BridgeRequest request, BridgeQuery query, int total)
        {
            int next = query.Offset + query.Limit;
            if (next < total)
            {
                result.Truncated = true;
                result.ContinuationCursor = BridgeCursor.Encode(request.SessionId, request.Command,
                    query.CursorScope, next);
                result.Add("hasMore", true);
            }
            else result.Add("hasMore", false);
        }
    }

    internal static class BridgeResultMutation
    {
        internal static BridgeResult WithMutation(this BridgeResult result, string summary)
        {
            result.MutationSummary = summary;
            return result;
        }
    }
}
