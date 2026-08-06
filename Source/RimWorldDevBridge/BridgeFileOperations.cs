using System;
using System.IO;
using System.Text;

namespace RimWorldDevBridge
{
    internal static class BridgeFileOperations
    {
        internal static bool AtomicWrite(string path, string content)
        {
            string temp = null;
            try
            {
                temp = path + "." + Guid.NewGuid().ToString("N") + ".tmp";
                File.WriteAllText(temp, content, new UTF8Encoding(false));
                if (File.Exists(path)) File.Replace(temp, path, null, true);
                else File.Move(temp, path);
                return true;
            }
            catch
            {
                try { if (!string.IsNullOrEmpty(temp) && File.Exists(temp)) File.Delete(temp); } catch { }
                return false;
            }
        }

        internal static void TryDelete(string path)
        {
            try { if (File.Exists(path)) File.Delete(path); } catch { }
        }
    }
}
