using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Reflection;
using System.Security.Cryptography;
using Verse;

namespace RimWorldDevBridge
{
    internal static class BridgeHotAdapters
    {
        private sealed class Generation
        {
            public Assembly assembly;
            public string fileName;
            public string hash;
            public DateTime loadedAt;
        }

        private static readonly List<Generation> generations = new List<Generation>();
        private static readonly HashSet<string> loadedHashes = new HashSet<string>(StringComparer.Ordinal);
        private static readonly List<string> lastErrors = new List<string>();

        internal static string DirectoryPath
        {
            get
            {
                ModContentPack content = LoadedModManager.RunningModsListForReading.FirstOrDefault(mod =>
                    string.Equals(mod.PackageIdPlayerFacing, "Lan.RimWorldDevBridge", StringComparison.OrdinalIgnoreCase));
                string root = content?.RootDir;
                if (root.NullOrEmpty())
                {
                    string location = typeof(BridgeHotAdapters).Assembly.Location;
                    string assemblies = location.NullOrEmpty() ? null : Path.GetDirectoryName(location);
                    if (assemblies.NullOrEmpty()) throw new DirectoryNotFoundException("Could not resolve the RimWorld Dev Bridge mod root.");
                    root = Path.GetFullPath(Path.Combine(assemblies, "..", ".."));
                }
                return Path.Combine(root, "DevTools", "HotAdapters");
            }
        }

        internal static IReadOnlyList<Assembly> Assemblies => generations.Select(value => value.assembly).ToList();
        internal static int GenerationCount => generations.Count;
        internal static int ErrorCount => lastErrors.Count;
        internal static string FingerprintSource => string.Join("|",
            generations.Select(value => value.fileName + ":" + value.hash));

        internal static List<string> LoadChanged()
        {
            Directory.CreateDirectory(DirectoryPath);
            lastErrors.Clear();
            int loaded = 0;
            FileInfo[] files;
            try
            {
                files = new DirectoryInfo(DirectoryPath).GetFiles("*.dll")
                    .OrderBy(file => file.LastWriteTimeUtc)
                    .ThenBy(file => file.Name, StringComparer.OrdinalIgnoreCase)
                    .ToArray();
            }
            catch (Exception exception)
            {
                lastErrors.Add("scan " + Clean(exception.GetBaseException().Message));
                return StatusLines(0);
            }

            for (int i = 0; i < files.Length; i++)
            {
                try
                {
                    byte[] bytes = ReadAllBytesShared(files[i].FullName);
                    string hash;
                    using (SHA256 algorithm = SHA256.Create())
                        hash = Convert.ToBase64String(algorithm.ComputeHash(bytes));
                    if (loadedHashes.Contains(hash)) continue;
                    Assembly assembly = Assembly.Load(bytes);
                    generations.Add(new Generation
                    {
                        assembly = assembly,
                        fileName = files[i].Name,
                        hash = hash,
                        loadedAt = DateTime.UtcNow
                    });
                    loadedHashes.Add(hash);
                    loaded++;
                }
                catch (Exception exception)
                {
                    lastErrors.Add(files[i].Name + " " + Clean(exception.GetBaseException().Message));
                }
            }
            return StatusLines(loaded);
        }

        internal static bool IsHot(Assembly assembly) =>
            assembly != null && generations.Any(value => ReferenceEquals(value.assembly, assembly));

        internal static string LabelFor(Assembly assembly)
        {
            Generation generation = generations.LastOrDefault(value => ReferenceEquals(value.assembly, assembly));
            return generation == null ? assembly?.GetName().Name ?? "unknown" :
                Path.GetFileNameWithoutExtension(generation.fileName);
        }

        internal static List<string> Status() => StatusLines(0);

        private static List<string> StatusLines(int loaded)
        {
            var lines = new List<string>
            {
                "hotAdapters=" + DirectoryPath,
                "loadedNow=" + loaded,
                "retainedGenerations=" + generations.Count,
                "errors=" + lastErrors.Count
            };
            for (int i = Math.Max(0, generations.Count - 8); i < generations.Count; i++)
            {
                Generation value = generations[i];
                lines.Add("generation=" + (i + 1) + " file:" + value.fileName +
                    " assembly:" + value.assembly.GetName().Name + " loadedUtc:" + value.loadedAt.ToString("s"));
            }
            for (int i = 0; i < lastErrors.Count && i < 8; i++) lines.Add("error=" + lastErrors[i]);
            return lines;
        }

        private static byte[] ReadAllBytesShared(string path)
        {
            using (FileStream stream = new FileStream(path, FileMode.Open, FileAccess.Read,
                FileShare.ReadWrite | FileShare.Delete))
            {
                byte[] bytes = new byte[stream.Length];
                int offset = 0;
                while (offset < bytes.Length)
                {
                    int read = stream.Read(bytes, offset, bytes.Length - offset);
                    if (read <= 0) throw new EndOfStreamException("Unexpected end of adapter file.");
                    offset += read;
                }
                return bytes;
            }
        }

        private static string Clean(string value) =>
            (value ?? "unknown").Replace('|', '/').Replace('\r', ' ').Replace('\n', ' ');
    }
}
