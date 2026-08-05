using System;
using System.Diagnostics;
using UnityEngine;
using Verse;

namespace RimWorldDevBridge
{
    public sealed class RimWorldDevBridgeMod : Mod
    {
        internal static BridgeSettings Settings;

        public RimWorldDevBridgeMod(ModContentPack content) : base(content)
        {
            long constructionStart = Stopwatch.GetTimestamp();
            long managedBefore = GC.GetTotalMemory(false);
            Settings = GetSettings<BridgeSettings>();
            BridgeRuntime.Bootstrap(content.RootDir, constructionStart, managedBefore);
        }

        public override string SettingsCategory() => "RimWorld Dev Bridge";

        public override void WriteSettings()
        {
            Settings.Normalize();
            base.WriteSettings();
            BridgeRuntime.ApplySchedulerSettings();
            BridgeRuntime.ApplyRemoteMutationSettings();
        }

        public override void DoSettingsWindowContents(Rect inRect)
        {
            Listing_Standard listing = new Listing_Standard();
            listing.Begin(inRect);
            listing.CheckboxLabeled("Allow remote mutation leases (in-game confirmation still required)",
                ref Settings.RemoteMutationEnabled,
                "Disabled by default. Enabling this never authorizes a write by itself; the current game must also be explicitly confirmed in the bridge warning panel.");
            listing.Label("Operation queue capacity: requested " + Settings.QueueCapacity +
                ", effective " + BridgeRuntime.EffectiveQueueCapacity);
            Settings.QueueCapacity = (int)listing.Slider(Settings.QueueCapacity, 8, 256);
            listing.Label("Connected client limit: " + Settings.ConnectedClientLimit);
            Settings.ConnectedClientLimit = (int)listing.Slider(Settings.ConnectedClientLimit, 2, 32);
            listing.Label("Main-thread budget per drain: requested " + Settings.MainThreadBudgetMs +
                ", effective " + BridgeRuntime.EffectiveMainThreadBudgetMs + " ms");
            Settings.MainThreadBudgetMs = (int)listing.Slider(Settings.MainThreadBudgetMs, 1, 12);
            listing.CheckboxLabeled("Show the read-only indicator while the bridge is idle", ref Settings.ShowBridgeIndicator,
                "Active transport and every write lease remain visible, including live-confirmed access.");
            listing.Label("Indicator position: " + BridgeIndicatorPosition(Settings.BridgeIndicatorCorner));
            Settings.BridgeIndicatorCorner = (int)listing.Slider(Settings.BridgeIndicatorCorner, 0, 3);
            if (BridgeRuntime.SchedulerSettingsPending)
                listing.Label("Scheduler changes apply when settings are applied; queued and running requests are preserved.");
            listing.End();
        }

        private static string BridgeIndicatorPosition(int corner)
        {
            switch (corner)
            {
                case 1: return "top-left";
                case 2: return "bottom-right";
                case 3: return "bottom-left";
                default: return "top-right";
            }
        }
    }
}
