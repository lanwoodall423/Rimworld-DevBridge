using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Reflection;
using Verse;

namespace RimWorldDevBridge
{
    // Captures the owner-thread view of loaded mods and modules before indexing leaves the game thread.
    internal static class BridgeAdapterSourceDiscovery
    {
        private const int MaximumAdapterSources = 256;

        internal static void Capture(long generation, string legacyPath,
            out List<BridgeAdapterSourceRecord> sources, out HashSet<string> packages)
        {
            List<BridgeAdapterSourceRecord> captured = new List<BridgeAdapterSourceRecord>();
            packages = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
            foreach (ModContentPack mod in LoadedModManager.RunningModsListForReading ??
                Enumerable.Empty<ModContentPack>())
            {
                string packageId = mod?.PackageIdPlayerFacing;
                if (string.IsNullOrWhiteSpace(packageId) || string.IsNullOrWhiteSpace(mod.RootDir)) continue;
                string root;
                try { root = Path.GetFullPath(mod.RootDir); }
                catch { continue; }
                packages.Add(packageId);
                captured.Add(new BridgeAdapterSourceRecord(BridgeAdapterSourceKind.OwnerMod, packageId,
                    ReadLoadedModVersion(mod), root, "owner:" + packageId + "/DevTools/BridgeAdapters",
                    generation, CaptureLoadedModules(packageId, root)));
            }

            captured.Add(new BridgeAdapterSourceRecord(BridgeAdapterSourceKind.LegacyDevelopment,
                "Lan.RimWorldDevBridge", "legacy", Path.GetFullPath(legacyPath),
                "legacy:DevTools/HotAdapters", generation, Array.Empty<BridgeLoadedModuleRecord>()));
            sources = captured
                .GroupBy(item => item.SourceKind + "|" + item.PackageId + "|" + item.DirectoryPath,
                    StringComparer.OrdinalIgnoreCase)
                .Select(group => group.First())
                .OrderBy(item => item.SourceKind)
                .ThenBy(item => item.PackageId, StringComparer.OrdinalIgnoreCase)
                .ThenBy(item => item.DirectoryPath, StringComparer.OrdinalIgnoreCase)
                .Take(MaximumAdapterSources)
                .ToList();
        }

        private static string ReadLoadedModVersion(ModContentPack mod)
        {
            try
            {
                object metadata = mod.GetType().GetProperty("ModMetaData",
                    BindingFlags.Instance | BindingFlags.Public | BindingFlags.NonPublic)?.GetValue(mod, null);
                object version = metadata?.GetType().GetProperty("ModVersion",
                    BindingFlags.Instance | BindingFlags.Public | BindingFlags.NonPublic)?.GetValue(metadata, null);
                return string.IsNullOrWhiteSpace(version?.ToString()) ? "unknown" : version.ToString();
            }
            catch { return "unknown"; }
        }

        private static IReadOnlyList<BridgeLoadedModuleRecord> CaptureLoadedModules(string packageId, string root)
        {
            List<BridgeLoadedModuleRecord> modules = new List<BridgeLoadedModuleRecord>();
            string prefix = root.TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar) +
                Path.DirectorySeparatorChar;
            foreach (Assembly assembly in AppDomain.CurrentDomain.GetAssemblies())
            {
                string path = BridgeAdapterAssemblyVerification.LoadedAssemblyPath(assembly);
                if (string.IsNullOrWhiteSpace(path) || !BridgeAdapterAssemblyVerification.IsWithin(path, root) ||
                    !File.Exists(path)) continue;
                try
                {
                    AssemblyName name = assembly.GetName();
                    modules.Add(new BridgeLoadedModuleRecord(packageId,
                        path.Substring(prefix.Length).Replace(Path.AltDirectorySeparatorChar,
                            Path.DirectorySeparatorChar), path, name.FullName,
                        assembly.ManifestModule.ModuleVersionId, new FileInfo(path).Length));
                }
                catch { }
            }
            return modules.OrderBy(item => item.RelativePath, StringComparer.OrdinalIgnoreCase).ToList();
        }
    }
}
