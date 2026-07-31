using System;
using System.IO;
using Verse;

namespace RimWorldDevBridge
{
    internal static class BridgePaths
    {
        internal const string Prefix = "RimWorld-DevBridge-";
        internal static string ModRoot { get; private set; }
        private static string userRootOverride;
        internal static string UserRoot => userRootOverride ??
            Path.Combine(GenFilePaths.SaveDataFolderPath, "RimWorldDevBridge");
        internal static string StatusPath => Path.Combine(GenFilePaths.SaveDataFolderPath, Prefix + "Status.txt");
        internal static string WakePath => Path.Combine(GenFilePaths.SaveDataFolderPath, Prefix + "Wake.request");
        internal static string InputPath => Path.Combine(GenFilePaths.SaveDataFolderPath, Prefix + "In.txt");
        internal static string OutputPath => Path.Combine(GenFilePaths.SaveDataFolderPath, Prefix + "Out.txt");
        internal static string AdapterPath => Path.Combine(ModRoot, "DevTools", "HotAdapters");
        internal static string MacroPath => Path.Combine(UserRoot, "Macros.xml");
        internal static string FeatureTestPath => Path.Combine(UserRoot, "FeatureTests");
        internal static string CapturePath => Path.Combine(UserRoot, "Captures");
        internal static string AuditPath => Path.Combine(UserRoot, "MutationAudit.log");
        internal static string ManifestPath => Path.Combine(ModRoot, "BRIDGE_MANIFEST.txt");

        internal static void Initialize(string modRoot)
        {
            ModRoot = Path.GetFullPath(modRoot ?? throw new ArgumentNullException(nameof(modRoot)));
        }

        internal static void SetUserRootForTests(string path)
        {
            userRootOverride = string.IsNullOrWhiteSpace(path) ? null : Path.GetFullPath(path);
        }

        internal static string SafeOutputPath(string category, string fileName)
        {
            string safeCategory = SafeName(category, "output");
            string safeFile = SafeName(Path.GetFileName(fileName), "result.txt");
            string directory = Path.Combine(UserRoot, safeCategory);
            Directory.CreateDirectory(directory);
            string result = Path.GetFullPath(Path.Combine(directory, safeFile));
            if (!result.StartsWith(Path.GetFullPath(UserRoot) + Path.DirectorySeparatorChar,
                StringComparison.OrdinalIgnoreCase))
                throw new InvalidOperationException("Output path escaped the bridge user-data directory.");
            return result;
        }

        private static string SafeName(string value, string fallback)
        {
            string candidate = string.IsNullOrWhiteSpace(value) ? fallback : value;
            foreach (char invalid in Path.GetInvalidFileNameChars()) candidate = candidate.Replace(invalid, '_');
            return candidate.Replace("..", "_");
        }
    }
}
