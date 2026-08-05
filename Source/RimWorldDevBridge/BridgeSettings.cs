using UnityEngine;
using Verse;

namespace RimWorldDevBridge
{
    public sealed class BridgeSettings : ModSettings
    {
        public bool RemoteMutationEnabled = false;
        public int QueueCapacity = 64;
        public int ConnectedClientLimit = 16;
        public int MainThreadBudgetMs = 3;
        public int RetainedAdapterRestartThreshold = 8;
        public bool ShowBridgeIndicator;
        public int BridgeIndicatorCorner;

        internal void Normalize()
        {
            QueueCapacity = Mathf.Clamp(QueueCapacity, 8, 256);
            ConnectedClientLimit = Mathf.Clamp(ConnectedClientLimit, 2, 32);
            MainThreadBudgetMs = Mathf.Clamp(MainThreadBudgetMs, 1, 12);
            RetainedAdapterRestartThreshold = Mathf.Clamp(RetainedAdapterRestartThreshold, 2, 32);
            BridgeIndicatorCorner = Mathf.Clamp(BridgeIndicatorCorner, 0, 3);
        }

        public override void ExposeData()
        {
            Scribe_Values.Look(ref RemoteMutationEnabled, "remoteMutationEnabled", false);
            Scribe_Values.Look(ref QueueCapacity, "queueCapacity", 64);
            Scribe_Values.Look(ref ConnectedClientLimit, "connectedClientLimit", 16);
            Scribe_Values.Look(ref MainThreadBudgetMs, "mainThreadBudgetMs", 3);
            Scribe_Values.Look(ref RetainedAdapterRestartThreshold, "retainedAdapterRestartThreshold", 8);
            Scribe_Values.Look(ref ShowBridgeIndicator, "showBridgeIndicator", false);
            Scribe_Values.Look(ref BridgeIndicatorCorner, "bridgeIndicatorCorner", 0);
            Normalize();
        }
    }
}
