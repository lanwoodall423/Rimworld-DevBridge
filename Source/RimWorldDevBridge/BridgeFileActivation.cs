using System;
using System.IO;

namespace RimWorldDevBridge
{
    // Owns the dormant file watcher and coalesces wake/input signals until the owner thread acts on them.
    internal sealed class BridgeFileActivation : IDisposable
    {
        private const string WakeFileName = BridgePaths.Prefix + "Wake.request";
        private const string InputFileName = BridgePaths.Prefix + "In.txt";
        private readonly Func<bool> shuttingDown;
        private readonly Func<bool> active;
        private readonly Action assertMainThread;
        private readonly Action startTransport;
        private readonly Action deleteWakeFile;
        private readonly BridgeWakeSignal wakeSignal = new BridgeWakeSignal();
        private readonly BridgeWakeSignal inputSignal = new BridgeWakeSignal();
        private FileSystemWatcher watcher;
        private volatile bool legacyInputPending;

        internal BridgeFileActivation(Func<bool> shuttingDown, Func<bool> active, Action assertMainThread,
            Action startTransport, Action deleteWakeFile)
        {
            this.shuttingDown = shuttingDown;
            this.active = active;
            this.assertMainThread = assertMainThread;
            this.startTransport = startTransport;
            this.deleteWakeFile = deleteWakeFile;
        }

        internal void Initialize()
        {
            assertMainThread();
            string saveFolder = Verse.GenFilePaths.SaveDataFolderPath;
            watcher = new FileSystemWatcher(saveFolder, BridgePaths.Prefix + "*")
            {
                NotifyFilter = NotifyFilters.FileName | NotifyFilters.LastWrite,
                IncludeSubdirectories = false,
                EnableRaisingEvents = true
            };
            watcher.Created += OnWakeFile;
            watcher.Changed += OnWakeFile;
            watcher.Renamed += OnWakeFile;
        }

        internal void SignalInput() => inputSignal.Signal();

        internal void SignalWake() => wakeSignal.Signal();

        internal void ProcessPendingSignals()
        {
            assertMainThread();
            if (wakeSignal.Consume())
            {
                deleteWakeFile();
                startTransport();
            }
            if (inputSignal.Consume()) legacyInputPending = true;
            if (legacyInputPending && !active()) startTransport();
        }

        internal bool TakeLegacyInput()
        {
            if (!legacyInputPending) return false;
            legacyInputPending = false;
            return true;
        }

        internal void ResetPending() => legacyInputPending = false;

        public void Dispose()
        {
            try { watcher?.Dispose(); } catch { }
            watcher = null;
        }

        private void OnWakeFile(object sender, FileSystemEventArgs args)
        {
            if (shuttingDown()) return;
            string name = Path.GetFileName(args.FullPath);
            if (name.Equals(WakeFileName, StringComparison.OrdinalIgnoreCase))
                wakeSignal.Signal();
            else if (name.Equals(InputFileName, StringComparison.OrdinalIgnoreCase))
                inputSignal.Signal();
        }
    }
}
