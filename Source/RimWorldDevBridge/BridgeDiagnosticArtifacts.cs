using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Security.Cryptography;
using System.Text;
using RimWorld;
using Verse;

namespace RimWorldDevBridge
{
    // Capture and diff handlers own artifact I/O and bounded serialization; live query collection stays in BridgeDiagnostics.
    internal static class BridgeDiagnosticArtifacts
    {
        internal static BridgeResult CaptureState(BridgeExecutionContext context)
        {
            string name = BridgeDiagnostics.SafeArtifactName(context.Request.Argument,
                "capture-" + DateTime.UtcNow.ToString("yyyyMMdd-HHmmssfff"));
            string path = BridgePaths.SafeOutputPath("Captures", name + ".state");
            List<string> lines = BuildCapture(context);
            byte[] bytes = Encoding.UTF8.GetBytes(string.Join("\n", lines));
            File.WriteAllBytes(path, bytes);
            return BridgeResult.Ok("core.captureState").Add("capture", name).Add("path", path)
                .Add("records", lines.Count).Add("bytes", bytes.Length).Add("sha256", Sha256(bytes));
        }

        internal static BridgeResult DiffState(BridgeRequest request)
        {
            Dictionary<string, string> options;
            try { options = BridgeProtocol.ParseOptions((request.Argument ?? string.Empty).Replace(';', '&')); }
            catch (Exception exception) { return BridgeResult.Fail(BridgeStatus.INVALID_ARGUMENT, "invalid_diff_options", exception.Message); }
            string beforeName = BridgeDiagnostics.SafeArtifactName(BridgeProtocol.Value(options, "before"), null);
            string afterName = BridgeDiagnostics.SafeArtifactName(BridgeProtocol.Value(options, "after"), null);
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
                        BridgeText.Clean(thing.def?.defName) + "|" +
                        (thing.Spawned ? BridgeDiagnostics.Cell(thing.Position) : "unspawned") +
                        "|stack:" + thing.stackCount + "|hp:" +
                        (thing.def?.useHitPoints == true ? thing.HitPoints : -1));
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

        internal static string Sha256(byte[] bytes)
        {
            using (SHA256 algorithm = SHA256.Create())
                return string.Concat(algorithm.ComputeHash(bytes).Select(value => value.ToString("x2")));
        }
    }
}
