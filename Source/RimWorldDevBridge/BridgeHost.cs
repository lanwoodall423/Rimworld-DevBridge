using System;
using System.Collections;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Linq;
using System.Net;
using System.Net.Sockets;
using System.Reflection;
using System.Security.Cryptography;
using System.Text;
using System.Threading;
using HarmonyLib;
using RimWorld;
using UnityEngine;
using Verse;

namespace RimWorldDevBridge
{
    public sealed class RimWorldDevBridgeMod : Mod
    {
        internal static string RootDir;

        public RimWorldDevBridgeMod(ModContentPack content) : base(content)
        {
            RootDir = content.RootDir;
            new Harmony("lan.rimworld.devbridge").PatchAll();
        }
    }

    [HarmonyPatch(typeof(Game), nameof(Game.FinalizeInit))]
    public static class BridgeStartupPatch
    {
        public static void Postfix() => LongEventHandler.ExecuteWhenFinished(BridgeHost.Initialize);
    }

    public static class BridgeHost
    {
        private const string BridgeVersion = "1.4.1";
        private const int ProtocolVersion = 9;
        private const string CoreSchema = "e12b709c";
        private const string SessionContext =
            "context=test-save representativePlayerBehavior:false livePlay:only-when-user-directed";
        private const string Prefix = "RimWorld-DevBridge-";
        private const string InFile = Prefix + "In.txt";
        private const string OutFile = Prefix + "Out.txt";
        private const string StatusFile = Prefix + "Status.txt";
        private const string WakeFile = Prefix + "Wake.request";
        private const int SessionIdleSeconds = 180;
        private const int MaxConcurrentClients = 16;
        private static SynchronizationContext mainContext;
        private static FileSystemWatcher watcher;
        private static Timer sessionTimer;
        private static TcpListener listener;
        private static Thread tcpThread;
        private static volatile bool running;
        private static volatile bool initialized;
        private static int generation;
        private static int activeClients;
        private static int port;
        private static string token = "";
        private static long lastActivity;
        private static readonly Dictionary<string, ProviderCommand> providerCommands =
            new Dictionary<string, ProviderCommand>(StringComparer.OrdinalIgnoreCase);
        private static readonly Dictionary<string, AdapterDescriptor> adapterDescriptors =
            new Dictionary<string, AdapterDescriptor>(StringComparer.OrdinalIgnoreCase);
        private static readonly string[] CoreCommands =
        {
            "PING", "SYNC", "HELP", "STATUS", "SELF_TEST", "FEATURE_TESTS", "RUN_FEATURE_TESTS",
            "RELOAD_BRIDGE", "RELOAD_ADAPTERS", "HOT_STATUS",
            "RELOAD_HOT_ADAPTERS", "HOT_ADAPTER_STATUS",
            "SNAPSHOT", "MAPS", "PAWNS", "PAWN",
            "THINGS", "THING", "DEFS", "SELECTED", "SELECT", "JUMP", "COMPONENTS", "COMPONENT",
            "INSPECT", "UI_STATE", "SET_SPEED", "BATCH"
        };

        public static string SaveFolder => GenFilePaths.SaveDataFolderPath;
        public static string InputPath => Path.Combine(SaveFolder, InFile);
        public static string OutputPath => Path.Combine(SaveFolder, OutFile);
        public static string StatusPath => Path.Combine(SaveFolder, StatusFile);
        public static string WakePath => Path.Combine(SaveFolder, WakeFile);
        public static string ManifestPath => Path.Combine(ModRoot, "BRIDGE_MANIFEST.txt");

        private static string ModRoot
        {
            get
            {
                if (!RimWorldDevBridgeMod.RootDir.NullOrEmpty())
                    return RimWorldDevBridgeMod.RootDir;
                string assemblies = Path.GetDirectoryName(typeof(BridgeHost).Assembly.Location);
                return Path.GetFullPath(Path.Combine(assemblies, "..", ".."));
            }
        }

        public static void Initialize()
        {
            Shutdown();
            if ((!Prefs.DevMode && !GenCommandLine.CommandLineArgPassed("rimworlddevbridge")) ||
                Find.CurrentMap == null) return;
            mainContext = SynchronizationContext.Current;
            initialized = true;
            ReloadProviders();
            TryDelete(InputPath);
            TryDelete(WakePath);
            watcher = new FileSystemWatcher(SaveFolder, Prefix + "*")
            {
                NotifyFilter = NotifyFilters.FileName | NotifyFilters.LastWrite,
                IncludeSubdirectories = false,
                EnableRaisingEvents = true
            };
            watcher.Created += OnFile;
            watcher.Changed += OnFile;
            watcher.Renamed += OnFile;
            WriteStatus("DORMANT");
            WriteResponse("activate", "OK", CompactStatus());
        }

        public static void Shutdown()
        {
            initialized = false;
            try { watcher?.Dispose(); } catch { }
            watcher = null;
            StopTcp(false);
            TryDelete(InputPath);
            TryDelete(WakePath);
        }

        private static void OnFile(object sender, FileSystemEventArgs args)
        {
            if (!initialized || mainContext == null) return;
            string name = Path.GetFileName(args.FullPath);
            if (name.Equals(WakeFile, StringComparison.OrdinalIgnoreCase))
                mainContext.Post(_ => { TryDelete(WakePath); StartTcp(); }, null);
            else if (name.Equals(InFile, StringComparison.OrdinalIgnoreCase))
                mainContext.Post(_ => ProcessFile(), null);
            else if (name.Equals(BridgeMacros.ModuleFileName, StringComparison.OrdinalIgnoreCase))
                mainContext.Post(_ =>
                {
                    BridgeMacros.Reload();
                    WriteStatus(running ? "ON" : "DORMANT");
                }, null);
        }

        private static void ProcessFile()
        {
            if (!initialized || !File.Exists(InputPath)) return;
            string raw = "";
            try
            {
                raw = File.ReadAllText(InputPath).Trim();
                if (raw.NullOrEmpty()) return;
                File.Delete(InputPath);
                Execute(raw, true);
            }
            catch (IOException) { }
            catch (Exception exception) { ExceptionResponse(raw, exception, true); }
        }

        private static string Execute(string raw, bool writeFile)
        {
            string[] parts = Parse(raw);
            string id = parts[0].NullOrEmpty() ? "unknown" : parts[0];
            string command = parts.Length > 1 ? parts[1].Trim().ToUpperInvariant() : "";
            string argument = parts.Length > 2 ? parts[2].Trim() : "";
            try
            {
                List<string> lines = ExecuteCore(command, argument);
                if (lines == null && providerCommands.TryGetValue(command, out ProviderCommand provider))
                    lines = provider.Execute(argument, Find.CurrentMap);
                if (lines == null && BridgeMacros.TryExecute(command, argument,
                    ExecuteRegistered, out List<string> macroLines))
                    lines = macroLines;
                if (lines == null)
                    return Complete(id, "ERROR", new[] { "unknown=" + Clean(command), "use=HELP" }, writeFile);
                return Complete(id, "OK", Limit(lines, 120), writeFile);
            }
            catch (Exception exception)
            {
                return ExceptionResponse(raw, exception, writeFile);
            }
        }

        private static List<string> ExecuteCore(string command, string argument)
        {
            Map map = Find.CurrentMap;
            switch (command)
            {
                case "PING": return new List<string> { "pong", "tick=" + (Find.TickManager?.TicksGame ?? -1) };
                case "SYNC": return Sync(argument);
                case "HELP": return Help();
                case "STATUS": return CompactStatus();
                case "SELF_TEST": return SelfTest();
                case "FEATURE_TESTS": return BridgeFeatureTests.Status();
                case "RUN_FEATURE_TESTS": return BridgeFeatureTests.RunForBridge();
                case "RELOAD_BRIDGE":
                    ReloadProviders();
                    List<string> reload = BridgeMacros.Reload();
                    WriteStatus(running ? "ON" : "DORMANT");
                    return reload;
                case "RELOAD_ADAPTERS":
                    ReloadProviders();
                    WriteStatus(running ? "ON" : "DORMANT");
                    return new List<string> { "adapters=reloaded", "commands=" + providerCommands.Count };
                case "RELOAD_HOT_ADAPTERS":
                    ReloadProviders();
                    WriteStatus(running ? "ON" : "DORMANT");
                    return new List<string> { "hotAdapters=reloaded", "commands=" + providerCommands.Count }
                        .Concat(BridgeHotAdapters.Status()).ToList();
                case "HOT_ADAPTER_STATUS": return BridgeHotAdapters.Status();
                case "HOT_STATUS": return BridgeMacros.Status();
                case "SNAPSHOT": return Snapshot(map);
                case "MAPS": return Maps();
                case "PAWNS": return Pawns(map, argument);
                case "PAWN": return PawnDetails(map, ParseInt(argument));
                case "THINGS": return Things(map, argument);
                case "THING": return ThingDetails(map, ParseInt(argument));
                case "DEFS": return Defs(argument);
                case "SELECTED": return Selected();
                case "SELECT": return Select(map, ParseInt(argument));
                case "JUMP": return Jump(map, argument);
                case "COMPONENTS": return Components(map);
                case "COMPONENT": return Component(map, argument);
                case "INSPECT": return InspectCommand(map, argument);
                case "UI_STATE": return UiState();
                case "SET_SPEED": return SetSpeed(argument);
                case "BATCH": return Batch(argument);
                default: return null;
            }
        }

        private static List<string> Help()
        {
            List<string> lines = new List<string>
            {
                "protocol=v" + ProtocolVersion + " request=id|COMMAND|argument",
                SessionContext,
                "core=" + string.Join(",", CoreCommands),
                "adapters=" + string.Join(",", providerCommands.Values.Select(value => value.adapter).Distinct().OrderBy(value => value)),
                "hot=" + string.Join(",", BridgeMacros.CommandNames)
            };
            lines.AddRange(providerCommands.Values.OrderBy(value => value.name).Select(value =>
                "cmd=" + value.name + " mode:" + (value.mutating ? "write" : "read") +
                " adapter:" + value.adapter + " desc:" + Clean(value.description)));
            return lines;
        }

        internal static List<string> ExecuteRegistered(string command, string argument)
        {
            string name = (command ?? "").Trim().ToUpperInvariant();
            List<string> lines = ExecuteCore(name, argument ?? "");
            if (lines == null && providerCommands.TryGetValue(name, out ProviderCommand provider))
                lines = provider.Execute(argument, Find.CurrentMap);
            return lines;
        }

        private static List<string> CompactStatus() => new List<string>
        {
            "bridge=RimWorldDevBridge version:" + BridgeVersion + " protocol:v" +
                ProtocolVersion + " state:" + (running ? "ON" : "DORMANT"),
            "game=" + VersionControl.CurrentVersionStringWithoutBuild + " dev:" + Prefs.DevMode,
            SessionContext,
            "map=" + (Find.CurrentMap?.uniqueID.ToString() ?? "none") +
                " tick:" + (Find.TickManager?.TicksGame ?? -1),
            "adapters=" + providerCommands.Values.Select(value => value.adapter).Distinct().Count() +
                " commands:" + (CoreCommands.Length + providerCommands.Count),
            "clients=active:" + Math.Max(0, Volatile.Read(ref activeClients)) +
                " max:" + MaxConcurrentClients,
            "hotAdapterGenerations=" + BridgeHotAdapters.GenerationCount + " errors:" + BridgeHotAdapters.ErrorCount
        };

        private static List<string> Sync(string knownFingerprint)
        {
            int hotBefore = BridgeHotAdapters.GenerationCount;
            BridgeHotAdapters.LoadChanged();
            if (BridgeHotAdapters.GenerationCount != hotBefore)
            {
                ReloadProviders();
                WriteStatus(running ? "ON" : "DORMANT");
            }
            Dictionary<string, string> disk = ReadKeyValues(ManifestPath);
            string fingerprint = RuntimeFingerprint();
            bool same = string.Equals((knownFingerprint ?? "").Trim(), fingerprint,
                StringComparison.OrdinalIgnoreCase);
            string diskVersion = ValueOr(disk, "bridge", "missing");
            string diskProtocol = ValueOr(disk, "protocol", "missing");
            string diskSchema = ValueOr(disk, "schema", "missing");
            bool restart = diskVersion != BridgeVersion ||
                diskProtocol != ProtocolVersion.ToString() || diskSchema != CoreSchema;
            string adapters = adapterDescriptors.Count == 0 ? "none" :
                string.Join(",", adapterDescriptors.Values.OrderBy(value => value.name)
                    .Select(value => value.name + ":" + value.version));
            List<string> lines = new List<string>
            {
                "sync=" + (same ? "same" : "changed") + " fp:" + fingerprint +
                    " bridge:" + BridgeVersion + " p:" + ProtocolVersion +
                    " restart:" + (restart ? "1" : "0"),
                SessionContext,
                "adapters=" + adapters
            };
            if (!same)
            {
                lines.Add("core=" + CoreSchema + " disk:" + diskSchema +
                    " hot:" + BridgeMacros.Generation +
                    " hotAdapters:" + BridgeHotAdapters.GenerationCount);
                lines.Add("changes=" + Clean(ValueOr(disk, "changes", "none")));
                string adapterChanges = string.Join(";",
                    adapterDescriptors.Values.Where(value => !value.changes.NullOrEmpty())
                        .OrderBy(value => value.name)
                        .Select(value => value.name + ":" + Clean(value.changes)));
                if (!adapterChanges.NullOrEmpty()) lines.Add("adapterChanges=" + adapterChanges);
            }
            return lines;
        }

        private static List<string> SelfTest()
        {
            string[] parsed = Parse("test|PING|value");
            Dictionary<string, string> disk = ReadKeyValues(ManifestPath);
            bool passed = parsed.Length == 3 && parsed[0] == "test" && parsed[1] == "PING" &&
                parsed[2] == "value" && CoreCommands.Distinct().Count() == CoreCommands.Length &&
                IPAddress.IsLoopback(IPAddress.Parse("127.0.0.1")) &&
                Path.GetFileName(StatusPath) == StatusFile &&
                ValueOr(disk, "bridge", "") == BridgeVersion &&
                ValueOr(disk, "protocol", "") == ProtocolVersion.ToString() &&
                ValueOr(disk, "schema", "") == CoreSchema &&
                SessionContext.Contains("representativePlayerBehavior:false") &&
                MaxConcurrentClients >= 4 &&
                Volatile.Read(ref activeClients) >= 0 &&
                RuntimeFingerprint().Length == 12;
            return new List<string>
            {
                "selfTest=" + (passed ? "PASS" : "FAIL"),
                "coreCommands=" + CoreCommands.Length,
                "adapterCommands=" + providerCommands.Count,
                "hotCommands=" + BridgeMacros.CommandNames.Count(),
                "hotAdapterGenerations=" + BridgeHotAdapters.GenerationCount,
                "concurrentClients=" + MaxConcurrentClients
            };
        }

        private static List<string> Snapshot(Map map)
        {
            if (map == null) return new List<string> { "map=none" };
            IReadOnlyList<Pawn> pawns = map.mapPawns.AllPawnsSpawned;
            return new List<string>
            {
                "map=id:" + map.uniqueID + " tile:" + map.Tile + " size:" + map.Size.x + "x" + map.Size.z,
                "time=tick:" + Find.TickManager.TicksGame + " speed:" + Find.TickManager.CurTimeSpeed,
                "pawns=all:" + pawns.Count + " colonists:" + map.mapPawns.FreeColonistsSpawned.Count +
                    " animals:" + pawns.Count(pawn => pawn.RaceProps?.Animal == true) +
                    " hostile:" + pawns.Count(pawn => pawn.HostileTo(Faction.OfPlayer)),
                "things=" + map.listerThings.AllThings.Count + " components:" + map.components.Count,
                "selected=" + (Find.Selector?.SingleSelectedThing?.thingIDNumber.ToString() ?? "none") +
                    " windows:" + (Find.WindowStack?.Windows?.Count ?? 0)
            };
        }

        private static List<string> Maps() =>
            Find.Maps.Select(map => "map=id:" + map.uniqueID + " tile:" + map.Tile +
                " size:" + map.Size.x + "x" + map.Size.z + " current:" + (map == Find.CurrentMap)).ToList();

        private static List<string> Pawns(Map map, string filter)
        {
            if (map == null) return new List<string> { "map=none" };
            filter = filter ?? "";
            IEnumerable<Pawn> pawns = map.mapPawns.AllPawnsSpawned;
            if (!filter.NullOrEmpty())
                pawns = pawns.Where(pawn => pawn.LabelShort.IndexOf(filter, StringComparison.OrdinalIgnoreCase) >= 0 ||
                    pawn.def.defName.IndexOf(filter, StringComparison.OrdinalIgnoreCase) >= 0);
            return pawns.OrderBy(pawn => pawn.thingIDNumber).Take(80).Select(PawnLine).ToList();
        }

        private static string PawnLine(Pawn pawn) =>
            "pawn=id:" + pawn.thingIDNumber + " label:" + Clean(pawn.LabelShort) +
            " def:" + pawn.def.defName + " faction:" + (pawn.Faction?.Name ?? "none") +
            " pos:" + pawn.Position.x + "," + pawn.Position.z +
            " job:" + (pawn.CurJobDef?.defName ?? "none") +
            " health:" + pawn.health.summaryHealth.SummaryHealthPercent.ToString("0.00");

        private static List<string> PawnDetails(Map map, int id)
        {
            Pawn pawn = map?.mapPawns.AllPawnsSpawned.FirstOrDefault(value => value.thingIDNumber == id);
            if (pawn == null) return new List<string> { "pawn=not_found" };
            List<string> lines = new List<string> { PawnLine(pawn) };
            lines.Add("kind=" + (pawn.RaceProps?.Animal == true ? "animal" : "humanlike") +
                " gender:" + pawn.gender + " age:" + pawn.ageTracker.AgeBiologicalYearsFloat.ToString("0.0") +
                " drafted:" + pawn.Drafted + " downed:" + pawn.Downed + " mental:" + pawn.InMentalState);
            if (pawn.CurJob != null)
                lines.Add("job=def:" + pawn.CurJobDef.defName + " targetA:" + Clean(pawn.CurJob.targetA.ToString()) +
                    " targetB:" + Clean(pawn.CurJob.targetB.ToString()));
            lines.AddRange(InspectObject(pawn, 18).Select(value => "field=" + value));
            return lines;
        }

        private static List<string> Things(Map map, string filter)
        {
            if (map == null) return new List<string> { "map=none" };
            filter = filter ?? "";
            return map.listerThings.AllThings.Where(thing => filter.NullOrEmpty() ||
                    thing.def.defName.IndexOf(filter, StringComparison.OrdinalIgnoreCase) >= 0 ||
                    thing.LabelShort.IndexOf(filter, StringComparison.OrdinalIgnoreCase) >= 0)
                .OrderBy(thing => thing.thingIDNumber).Take(80).Select(ThingLine).ToList();
        }

        private static string ThingLine(Thing thing) =>
            "thing=id:" + thing.thingIDNumber + " type:" + thing.GetType().Name +
            " def:" + (thing.def?.defName ?? "none") + " label:" + Clean(thing.LabelShort) +
            " pos:" + (thing.Spawned ? thing.Position.x + "," + thing.Position.z : "unspawned");

        private static List<string> ThingDetails(Map map, int id)
        {
            Thing thing = map?.listerThings.AllThings.FirstOrDefault(value => value.thingIDNumber == id);
            if (thing == null) return new List<string> { "thing=not_found" };
            return new[] { ThingLine(thing) }.Concat(InspectObject(thing, 30).Select(value => "field=" + value)).ToList();
        }

        private static List<string> Defs(string argument)
        {
            string[] parts = (argument ?? "").Split(new[] { ':' }, 2);
            string kind = parts[0].NullOrEmpty() ? "thing" : parts[0].ToLowerInvariant();
            string filter = parts.Length > 1 ? parts[1] : "";
            IEnumerable<Def> defs = kind == "job" ? DefDatabase<JobDef>.AllDefsListForReading.Cast<Def>() :
                kind == "hediff" ? DefDatabase<HediffDef>.AllDefsListForReading.Cast<Def>() :
                kind == "research" ? DefDatabase<ResearchProjectDef>.AllDefsListForReading.Cast<Def>() :
                DefDatabase<ThingDef>.AllDefsListForReading.Cast<Def>();
            return defs.Where(def => filter.NullOrEmpty() ||
                    def.defName.IndexOf(filter, StringComparison.OrdinalIgnoreCase) >= 0 ||
                    (def.label ?? "").IndexOf(filter, StringComparison.OrdinalIgnoreCase) >= 0)
                .OrderBy(def => def.defName).Take(80)
                .Select(def => "def=" + def.defName + " label:" + Clean(def.label) +
                    " type:" + def.GetType().Name).ToList();
        }

        private static List<string> Selected()
        {
            Thing selected = Find.Selector?.SingleSelectedThing;
            return selected == null ? new List<string> { "selected=none" } :
                new[] { "selected=" + ThingLine(selected) }
                    .Concat(InspectObject(selected, 24).Select(value => "field=" + value)).ToList();
        }

        private static List<string> Select(Map map, int id)
        {
            Thing thing = map?.listerThings.AllThings.FirstOrDefault(value => value.thingIDNumber == id);
            if (thing == null) return new List<string> { "select=not_found" };
            Find.Selector.ClearSelection();
            Find.Selector.Select(thing);
            if (thing.Spawned) Find.CameraDriver.JumpToCurrentMapLoc(thing.Position);
            return new List<string> { "selected=" + id };
        }

        private static List<string> Jump(Map map, string argument)
        {
            string[] values = (argument ?? "").Split(',');
            if (map == null || values.Length < 2 || !int.TryParse(values[0], out int x) ||
                !int.TryParse(values[1], out int z))
                return new List<string> { "jump=invalid expected:x,z" };
            IntVec3 cell = new IntVec3(x, 0, z);
            if (!cell.InBounds(map)) return new List<string> { "jump=out_of_bounds" };
            Find.CameraDriver.JumpToCurrentMapLoc(cell);
            return new List<string> { "jump=" + x + "," + z };
        }

        private static List<string> Components(Map map)
        {
            if (map == null) return new List<string> { "map=none" };
            return map.components.OrderBy(component => component.GetType().FullName)
                .Select(component => "component=" + component.GetType().FullName).ToList();
        }

        private static List<string> Component(Map map, string filter)
        {
            MapComponent component = map?.components.FirstOrDefault(value =>
                value.GetType().FullName.IndexOf(filter ?? "", StringComparison.OrdinalIgnoreCase) >= 0);
            if (component == null) return new List<string> { "component=not_found" };
            return new[] { "component=" + component.GetType().FullName }
                .Concat(InspectObject(component, 50).Select(value => "field=" + value)).ToList();
        }

        private static List<string> InspectCommand(Map map, string argument)
        {
            object target;
            if ((argument ?? "").Equals("selected", StringComparison.OrdinalIgnoreCase) || argument.NullOrEmpty())
                target = Find.Selector?.SingleSelectedThing;
            else if (int.TryParse(argument, out int id))
                target = map?.listerThings.AllThings.FirstOrDefault(value => value.thingIDNumber == id);
            else
                target = map?.components.FirstOrDefault(value =>
                    value.GetType().FullName.IndexOf(argument, StringComparison.OrdinalIgnoreCase) >= 0);
            if (target == null) return new List<string> { "inspect=not_found" };
            return new[] { "inspect=type:" + target.GetType().FullName }
                .Concat(InspectObject(target, 60).Select(value => "field=" + value)).ToList();
        }

        private static IEnumerable<string> InspectObject(object target, int limit)
        {
            if (target == null) yield break;
            Type type = target.GetType();
            int count = 0;
            foreach (FieldInfo field in type.GetFields(BindingFlags.Instance | BindingFlags.Public)
                         .OrderBy(field => field.Name))
            {
                if (count++ >= limit) yield break;
                string value;
                try { value = SimpleValue(field.GetValue(target)); }
                catch { value = "<error>"; }
                yield return field.Name + ":" + value;
            }
            foreach (PropertyInfo property in type.GetProperties(BindingFlags.Instance | BindingFlags.Public)
                         .Where(property => property.GetIndexParameters().Length == 0 &&
                             property.GetMethod != null && SimpleType(property.PropertyType))
                         .OrderBy(property => property.Name))
            {
                if (count++ >= limit) yield break;
                string value;
                try { value = SimpleValue(property.GetValue(target, null)); }
                catch { value = "<error>"; }
                yield return property.Name + ":" + value;
            }
        }

        private static bool SimpleType(Type type) =>
            type.IsPrimitive || type.IsEnum || type == typeof(string) || type == typeof(decimal) ||
            type == typeof(IntVec3) || typeof(Def).IsAssignableFrom(type);

        private static string SimpleValue(object value)
        {
            if (value == null) return "null";
            if (value is Def def) return def.defName;
            if (value is string text) return Clean(text);
            if (value is ICollection collection) return "count:" + collection.Count;
            return Clean(Convert.ToString(value, System.Globalization.CultureInfo.InvariantCulture));
        }

        private static List<string> UiState()
        {
            List<string> lines = Selected();
            IList<Window> windows = Find.WindowStack?.Windows;
            lines.Add("windows=" + (windows?.Count ?? 0));
            if (windows != null)
                lines.AddRange(windows.Take(20).Select(window => "window=type:" +
                    window.GetType().FullName + " size:" + window.windowRect.width.ToString("0") +
                    "x" + window.windowRect.height.ToString("0")));
            return lines;
        }

        private static List<string> SetSpeed(string argument)
        {
            if (!int.TryParse(argument, out int speed) || speed < 0 || speed > 4)
                return new List<string> { "speed=invalid expected:0-4" };
            Find.TickManager.CurTimeSpeed = (TimeSpeed)speed;
            return new List<string> { "speed=" + speed };
        }

        private static List<string> Batch(string argument)
        {
            List<string> lines = new List<string>();
            string[] entries = (argument ?? "").Split(new[] { ';' }, StringSplitOptions.RemoveEmptyEntries);
            for (int i = 0; i < entries.Length && i < 12; i++)
            {
                string[] pair = entries[i].Split(new[] { ':' }, 2);
                string command = pair[0].Trim().ToUpperInvariant();
                string commandArgument = pair.Length > 1 ? pair[1] : "";
                List<string> result = ExecuteCore(command, commandArgument);
                if (result == null && providerCommands.TryGetValue(command, out ProviderCommand provider))
                    result = provider.Execute(commandArgument, Find.CurrentMap);
                lines.Add("section=" + command);
                lines.AddRange((result ?? new List<string> { "unsupported" }).Take(20));
            }
            return lines;
        }

        private static void ReloadProviders()
        {
            BridgeHotAdapters.LoadChanged();
            providerCommands.Clear();
            adapterDescriptors.Clear();
            foreach (Assembly assembly in AppDomain.CurrentDomain.GetAssemblies())
            {
                if (!BridgeHotAdapters.IsHot(assembly)) RegisterProviders(assembly, false, assembly.GetName().Name);
            }
            foreach (Assembly assembly in BridgeHotAdapters.Assemblies)
                RegisterProviders(assembly, true, BridgeHotAdapters.LabelFor(assembly));
            BridgeMacros.Initialize();
        }

        private static void RegisterProviders(Assembly assembly, bool allowOverride, string adapterLabel)
        {
            Type[] types;
            try { types = assembly.GetTypes(); }
            catch (ReflectionTypeLoadException exception)
            {
                types = exception.Types.Where(type => type != null).ToArray();
            }
            catch { return; }
            foreach (Type type in types)
            {
                MethodInfo specs = type.GetMethod("BridgeCommandSpecs",
                    BindingFlags.Public | BindingFlags.Static, null, Type.EmptyTypes, null);
                MethodInfo execute = type.GetMethod("ExecuteBridgeCommand",
                    BindingFlags.Public | BindingFlags.Static, null,
                    new[] { typeof(string), typeof(string), typeof(Map) }, null);
                if (specs == null || execute == null || specs.ReturnType != typeof(string[])) continue;
                string effectiveAdapter = RegisterAdapter(type, adapterLabel);
                string[] values;
                try { values = (string[])specs.Invoke(null, null); }
                catch { continue; }
                foreach (string value in values ?? Array.Empty<string>())
                {
                    string[] fields = (value ?? "").Split(new[] { '|' }, 3);
                    string name = fields[0].Trim().ToUpperInvariant();
                    if (name.NullOrEmpty() || CoreCommands.Contains(name) ||
                        (!allowOverride && providerCommands.ContainsKey(name))) continue;
                    providerCommands[name] = new ProviderCommand
                    {
                        name = name,
                        mutating = fields.Length > 1 && fields[1].Equals("W", StringComparison.OrdinalIgnoreCase),
                        description = fields.Length > 2 ? fields[2] : "",
                        adapter = effectiveAdapter,
                        method = execute
                    };
                }
            }
        }

        private static string RegisterAdapter(Type providerType, string fallback)
        {
            string name = fallback;
            string version = "?";
            string changes = "";
            MethodInfo info = providerType.GetMethod("BridgeAdapterInfo",
                BindingFlags.Public | BindingFlags.Static, null, Type.EmptyTypes, null);
            if (info?.ReturnType == typeof(string))
            {
                try
                {
                    string[] fields = (((string)info.Invoke(null, null)) ?? "").Split(new[] { '|' }, 3);
                    if (fields.Length > 0 && !fields[0].NullOrEmpty()) name = fields[0].Trim();
                    if (fields.Length > 1 && !fields[1].NullOrEmpty()) version = fields[1].Trim();
                    if (fields.Length > 2) changes = fields[2].Trim();
                }
                catch { }
            }
            adapterDescriptors[name] = new AdapterDescriptor
            {
                name = name,
                version = version,
                changes = changes
            };
            return name;
        }

        private static string RuntimeFingerprint()
        {
            StringBuilder value = new StringBuilder(CoreSchema);
            foreach (ProviderCommand command in providerCommands.Values.OrderBy(command => command.name))
                value.Append('|').Append(command.name).Append(':')
                    .Append(command.mutating ? 'W' : 'R').Append(':')
                    .Append(command.adapter).Append(':').Append(command.description);
            foreach (AdapterDescriptor adapter in adapterDescriptors.Values.OrderBy(adapter => adapter.name))
                value.Append("|A:").Append(adapter.name).Append(':').Append(adapter.version);
            foreach (string macro in BridgeMacros.CommandNames)
                value.Append("|H:").Append(macro);
            value.Append("|HM:").Append(BridgeMacros.FingerprintSource);
            value.Append("|HA:").Append(BridgeHotAdapters.FingerprintSource);
            using (SHA256 algorithm = SHA256.Create())
            {
                byte[] hash = algorithm.ComputeHash(Encoding.UTF8.GetBytes(value.ToString()));
                return string.Concat(hash.Take(6).Select(item => item.ToString("x2")));
            }
        }

        private static Dictionary<string, string> ReadKeyValues(string path)
        {
            Dictionary<string, string> values =
                new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
            try
            {
                foreach (string line in File.ReadAllLines(path))
                {
                    int separator = line.IndexOf('=');
                    if (separator > 0)
                        values[line.Substring(0, separator).Trim()] =
                            line.Substring(separator + 1).Trim();
                }
            }
            catch { }
            return values;
        }

        private static string ValueOr(Dictionary<string, string> values, string key, string fallback) =>
            values != null && values.TryGetValue(key, out string value) ? value : fallback;

        private static void StartTcp()
        {
            if (!initialized) return;
            if (running && listener != null)
            {
                Interlocked.Exchange(ref lastActivity, DateTime.UtcNow.Ticks);
                return;
            }
            StopTcp(false);
            try
            {
                token = Guid.NewGuid().ToString("N");
                listener = new TcpListener(IPAddress.Loopback, 0);
                listener.Start(MaxConcurrentClients);
                port = ((IPEndPoint)listener.LocalEndpoint).Port;
                running = true;
                int localGeneration = Interlocked.Increment(ref generation);
                TcpListener localListener = listener;
                string localToken = token;
                Interlocked.Exchange(ref lastActivity, DateTime.UtcNow.Ticks);
                tcpThread = new Thread(() => TcpLoop(localListener, localGeneration, localToken))
                {
                    IsBackground = true,
                    Name = "RimWorld Dev Bridge"
                };
                tcpThread.Start();
                sessionTimer = new Timer(_ =>
                {
                    if (!running || localGeneration != generation || mainContext == null) return;
                    if (DateTime.UtcNow.Ticks - Interlocked.Read(ref lastActivity) <
                        TimeSpan.FromSeconds(SessionIdleSeconds).Ticks) return;
                    mainContext.Post(__ =>
                    {
                        if (localGeneration == generation &&
                            DateTime.UtcNow.Ticks - Interlocked.Read(ref lastActivity) >=
                            TimeSpan.FromSeconds(SessionIdleSeconds).Ticks)
                            StopTcp(true);
                    }, null);
                }, null, 10000, 10000);
                WriteStatus("ON");
            }
            catch
            {
                StopTcp(false);
                WriteStatus("DORMANT");
            }
        }

        private static void StopTcp(bool writeDormant)
        {
            running = false;
            Interlocked.Increment(ref generation);
            try { sessionTimer?.Dispose(); } catch { }
            sessionTimer = null;
            try { listener?.Stop(); } catch { }
            listener = null;
            port = 0;
            token = "";
            if (writeDormant && initialized) WriteStatus("DORMANT");
        }

        private static void TcpLoop(TcpListener localListener, int localGeneration, string localToken)
        {
            while (running && localGeneration == generation)
            {
                TcpClient client = null;
                try
                {
                    client = localListener.AcceptTcpClient();
                    if (Interlocked.Increment(ref activeClients) > MaxConcurrentClients)
                    {
                        Interlocked.Decrement(ref activeClients);
                        RejectBusy(client);
                        client = null;
                        continue;
                    }
                    TcpClient acceptedClient = client;
                    client = null;
                    bool queued = false;
                    try
                    {
                        queued = ThreadPool.QueueUserWorkItem(_ => HandleClient(acceptedClient, localToken));
                    }
                    finally
                    {
                        if (!queued)
                        {
                            Interlocked.Decrement(ref activeClients);
                            acceptedClient.Close();
                        }
                    }
                }
                catch (SocketException) { if (!running || localGeneration != generation) return; }
                catch (ObjectDisposedException) { return; }
                catch { try { client?.Close(); } catch { } }
            }
        }

        private static void HandleClient(TcpClient client, string localToken)
        {
            try
            {
                client.NoDelay = true;
                client.ReceiveTimeout = 30000;
                client.SendTimeout = 30000;
                using (client)
                using (NetworkStream stream = client.GetStream())
                using (StreamReader reader = new StreamReader(stream, Encoding.UTF8, false, 4096, true))
                using (StreamWriter writer = new StreamWriter(stream, new UTF8Encoding(false), 4096, true)
                { AutoFlush = true, NewLine = "\n" })
                {
                    string raw = reader.ReadLine();
                    string prefix = localToken + "|";
                    if (raw.NullOrEmpty() || !raw.StartsWith(prefix, StringComparison.Ordinal))
                    {
                        writer.Write("id=unknown\nstatus=ERROR\nauth=failed");
                        return;
                    }
                    Interlocked.Exchange(ref lastActivity, DateTime.UtcNow.Ticks);
                    BridgeRequest request = new BridgeRequest { raw = raw.Substring(prefix.Length) };
                    SynchronizationContext context = mainContext;
                    if (context == null)
                    {
                        writer.Write("id=unknown\nstatus=ERROR\nbridge=main_thread_unavailable");
                        return;
                    }
                    try
                    {
                        context.Post(_ =>
                        {
                            try { request.response = Execute(request.raw, false); }
                            catch (Exception exception)
                            {
                                request.response = ExceptionResponse(request.raw, exception, false);
                            }
                            finally { request.done.Set(); }
                        }, null);
                    }
                    catch
                    {
                        writer.Write("id=unknown\nstatus=ERROR\nbridge=main_thread_unavailable");
                        return;
                    }
                    writer.Write(request.done.Wait(30000) ? request.response :
                        "id=unknown\nstatus=ERROR\nbridge=main_thread_timeout");
                    Interlocked.Exchange(ref lastActivity, DateTime.UtcNow.Ticks);
                }
            }
            catch { }
            finally
            {
                Interlocked.Decrement(ref activeClients);
            }
        }

        private static void RejectBusy(TcpClient client)
        {
            try
            {
                client.SendTimeout = 1000;
                using (client)
                using (NetworkStream stream = client.GetStream())
                using (StreamWriter writer = new StreamWriter(stream, new UTF8Encoding(false), 1024, true)
                { AutoFlush = true, NewLine = "\n" })
                    writer.Write("id=unknown\nstatus=BUSY\nretry=short");
            }
            catch { try { client?.Close(); } catch { } }
        }

        private static void WriteStatus(string state) => AtomicWrite(StatusPath, new[]
        {
            "bridge=" + state,
            "name=RimWorld Dev Bridge",
            "version=" + BridgeVersion,
            "protocol=" + ProtocolVersion,
            "schema=" + CoreSchema,
            "fingerprint=" + RuntimeFingerprint(),
            "transport=" + (port > 0 ? "tcp+file" : "wake-file"),
            "concurrency=" + Math.Max(0, Volatile.Read(ref activeClients)) + "/" + MaxConcurrentClients,
            "host=127.0.0.1",
            "port=" + port,
            "token=" + token,
            "adapters=" + string.Join(",", providerCommands.Values.Select(value => value.adapter).Distinct()),
            "hotGeneration=" + BridgeMacros.Generation,
            "hotModule=" + BridgeMacros.ModulePath,
            "hotAdapterDirectory=" + BridgeHotAdapters.DirectoryPath,
            "hotAdapterGenerations=" + BridgeHotAdapters.GenerationCount,
            "input=" + InputPath,
            "output=" + OutputPath,
            "tick=" + (Find.TickManager?.TicksGame ?? -1)
        });

        private static string Complete(string id, string status, IEnumerable<string> lines, bool writeFile)
        {
            List<string> response = new[] { "id=" + id, "status=" + status }
                .Concat(lines ?? Enumerable.Empty<string>()).ToList();
            if (writeFile) AtomicWrite(OutputPath, response);
            return string.Join("\n", response);
        }

        private static string ExceptionResponse(string raw, Exception exception, bool writeFile)
        {
            string[] parsed = Parse(raw ?? "");
            string id = parsed.Length > 0 && !parsed[0].NullOrEmpty() ? parsed[0] : "unknown";
            Exception root = exception.GetBaseException();
            string at = string.Join("<-", (new StackTrace(root, false).GetFrames() ?? Array.Empty<StackFrame>())
                .Take(3).Select(frame => (frame.GetMethod()?.DeclaringType?.Name ?? "?") + "." +
                    (frame.GetMethod()?.Name ?? "?")));
            return Complete(id, "ERROR", new[]
            {
                "exception=" + root.GetType().Name,
                "message=" + Clean(root.Message),
                "at=" + Clean(at)
            }, writeFile);
        }

        private static string[] Parse(string raw)
        {
            string line = raw.Replace('\r', '\n').Split(new[] { '\n' },
                StringSplitOptions.RemoveEmptyEntries).FirstOrDefault() ?? "";
            return line.Split(new[] { '|' }, 3);
        }

        private static IEnumerable<string> Limit(IEnumerable<string> lines, int max)
        {
            List<string> values = (lines ?? Enumerable.Empty<string>()).Take(max + 1).ToList();
            if (values.Count <= max) return values;
            return values.Take(max).Concat(new[] { "truncated=true limit:" + max });
        }

        private static int ParseInt(string value) => int.TryParse(value, out int parsed) ? parsed : -1;
        private static string Clean(string value) =>
            (value ?? "none").Replace('|', '/').Replace('\r', ' ').Replace('\n', ' ');

        private static void WriteResponse(string id, string status, IEnumerable<string> lines) =>
            AtomicWrite(OutputPath, new[] { "id=" + id, "status=" + status }.Concat(lines));

        private static void AtomicWrite(string path, IEnumerable<string> lines)
        {
            string temp = path + ".tmp";
            File.WriteAllLines(temp, lines);
            if (File.Exists(path)) File.Delete(path);
            File.Move(temp, path);
        }

        private static void TryDelete(string path)
        {
            try { if (File.Exists(path)) File.Delete(path); }
            catch (IOException) { }
        }

        private sealed class ProviderCommand
        {
            public string name;
            public bool mutating;
            public string description;
            public string adapter;
            public MethodInfo method;

            public List<string> Execute(string argument, Map map)
            {
                object result = method.Invoke(null, new object[] { name, argument ?? "", map });
                return result as List<string> ??
                    (result as IEnumerable<string>)?.ToList() ??
                    new List<string> { "adapter=empty_response" };
            }
        }

        private sealed class AdapterDescriptor
        {
            public string name;
            public string version;
            public string changes;
        }

        private sealed class BridgeRequest
        {
            public string raw;
            public string response;
            public readonly ManualResetEventSlim done = new ManualResetEventSlim(false);
        }
    }
}
