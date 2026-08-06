using System;
using System.IO;
using System.Linq;
using System.Reflection;
using System.Security.Cryptography;

namespace RimWorldDevBridge
{
    // File, loaded-module, and byte identity checks are kept off the catalog state machine.
    internal static class BridgeAdapterAssemblyVerification
    {
        private const long MaximumAdapterBytes = 64L * 1024L * 1024L;

        internal static bool IsWithin(string path, string root)
        {
            try
            {
                string fullPath = Path.GetFullPath(path);
                string fullRoot = Path.GetFullPath(root).TrimEnd(Path.DirectorySeparatorChar,
                    Path.AltDirectorySeparatorChar) + Path.DirectorySeparatorChar;
                return fullPath.StartsWith(fullRoot, StringComparison.OrdinalIgnoreCase);
            }
            catch { return false; }
        }

        internal static bool IsSafeFile(string path, string root)
        {
            try
            {
                FileInfo info = new FileInfo(path);
                if ((info.Attributes & FileAttributes.ReparsePoint) != 0) return false;
                DirectoryInfo directory = info.Directory;
                while (directory != null && !string.Equals(directory.FullName,
                    Path.GetFullPath(root).TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar),
                    StringComparison.OrdinalIgnoreCase))
                {
                    if ((directory.Attributes & FileAttributes.ReparsePoint) != 0) return false;
                    directory = directory.Parent;
                }
                return directory != null && IsWithin(info.FullName, root);
            }
            catch { return false; }
        }

        internal static bool IsSafeRelativePath(string value)
        {
            if (string.IsNullOrWhiteSpace(value) || Path.IsPathRooted(value) || value.Contains(":") ||
                value.IndexOf('\0') >= 0) return false;
            string[] parts = value.Replace('/', Path.DirectorySeparatorChar).Replace('\\', Path.DirectorySeparatorChar)
                .Split(new[] { Path.DirectorySeparatorChar }, StringSplitOptions.None);
            return parts.Length > 0 && parts.All(part => part.Length > 0 && part != "." && part != "..");
        }

        internal static bool IsSafeAssemblyFileName(string value)
        {
            return IsSafeRelativePath(value) && value.IndexOf(Path.DirectorySeparatorChar) < 0 &&
                value.IndexOf(Path.AltDirectorySeparatorChar) < 0 &&
                value.EndsWith(".dll", StringComparison.OrdinalIgnoreCase) &&
                !value.EndsWith(".tmp.dll", StringComparison.OrdinalIgnoreCase);
        }

        internal static string HashFile(string path, long length)
        {
            if (length <= 0 || length > MaximumAdapterBytes) throw new InvalidDataException("adapter size is invalid");
            using (SHA256 algorithm = SHA256.Create())
            using (FileStream stream = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.Read))
            {
                byte[] buffer = new byte[64 * 1024];
                int read;
                while ((read = stream.Read(buffer, 0, buffer.Length)) > 0)
                    algorithm.TransformBlock(buffer, 0, read, buffer, 0);
                algorithm.TransformFinalBlock(Array.Empty<byte>(), 0, 0);
                return string.Concat(algorithm.Hash.Select(value => value.ToString("X2")));
            }
        }

        internal static string Hash(byte[] bytes)
        {
            using (SHA256 algorithm = SHA256.Create())
                return string.Concat(algorithm.ComputeHash(bytes).Select(value => value.ToString("X2")));
        }

        internal static byte[] ReadAllBytesShared(string path)
        {
            using (FileStream stream = new FileStream(path, FileMode.Open, FileAccess.Read,
                FileShare.ReadWrite | FileShare.Delete))
            {
                byte[] bytes = new byte[stream.Length];
                int offset = 0;
                while (offset < bytes.Length)
                {
                    int read = stream.Read(bytes, offset, bytes.Length - offset);
                    if (read == 0) throw new EndOfStreamException("Adapter changed while being read.");
                    offset += read;
                }
                return bytes;
            }
        }

        internal static string LoadedAssemblyPath(Assembly assembly)
        {
            try
            {
                if (!string.IsNullOrWhiteSpace(assembly.Location)) return Path.GetFullPath(assembly.Location);
                if (!string.IsNullOrWhiteSpace(assembly.CodeBase) && Uri.TryCreate(assembly.CodeBase,
                    UriKind.Absolute, out Uri uri) && uri.IsFile) return Path.GetFullPath(uri.LocalPath);
            }
            catch { }
            return null;
        }

        internal static bool IsPrepatcherShadowPath(string path)
        {
            try
            {
                string fileName = Path.GetFileName(path);
                string parent = Path.GetFileName(Path.GetDirectoryName(path));
                return fileName.StartsWith("data-", StringComparison.OrdinalIgnoreCase) &&
                    parent.Equals("Mods", StringComparison.OrdinalIgnoreCase);
            }
            catch { return false; }
        }
    }
}
