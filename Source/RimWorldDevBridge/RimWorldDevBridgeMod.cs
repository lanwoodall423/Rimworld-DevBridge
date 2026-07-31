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

        public override void DoSettingsWindowContents(Rect inRect)
        {
            Listing_Standard listing = new Listing_Standard();
            listing.Begin(inRect);
            listing.CheckboxLabeled("Allow explicitly leased remote mutations", ref Settings.RemoteMutationEnabled,
                "Dev mode alone never authorizes a write. Disable this to force every remote command read-only.");
            listing.Label("Operation queue capacity: " + Settings.QueueCapacity);
            Settings.QueueCapacity = (int)listing.Slider(Settings.QueueCapacity, 8, 256);
            listing.Label("Connected client limit: " + Settings.ConnectedClientLimit);
            Settings.ConnectedClientLimit = (int)listing.Slider(Settings.ConnectedClientLimit, 2, 32);
            listing.Label("Main-thread budget per drain: " + Settings.MainThreadBudgetMs + " ms");
            Settings.MainThreadBudgetMs = (int)listing.Slider(Settings.MainThreadBudgetMs, 1, 12);
            listing.End();
        }
    }
}
