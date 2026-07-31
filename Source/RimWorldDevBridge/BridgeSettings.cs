using UnityEngine;
using Verse;

namespace RimWorldDevBridge
{
    public sealed class BridgeSettings : ModSettings
    {
        public bool RemoteMutationEnabled = true;
        public int QueueCapacity = 64;
        public int ConnectedClientLimit = 16;
        public int MainThreadBudgetMs = 3;
        public int RetainedAdapterRestartThreshold = 8;

        public override void ExposeData()
        {
            Scribe_Values.Look(ref RemoteMutationEnabled, "remoteMutationEnabled", true);
            Scribe_Values.Look(ref QueueCapacity, "queueCapacity", 64);
            Scribe_Values.Look(ref ConnectedClientLimit, "connectedClientLimit", 16);
            Scribe_Values.Look(ref MainThreadBudgetMs, "mainThreadBudgetMs", 3);
            Scribe_Values.Look(ref RetainedAdapterRestartThreshold, "retainedAdapterRestartThreshold", 8);
            QueueCapacity = Mathf.Clamp(QueueCapacity, 8, 256);
            ConnectedClientLimit = Mathf.Clamp(ConnectedClientLimit, 2, 32);
            MainThreadBudgetMs = Mathf.Clamp(MainThreadBudgetMs, 1, 12);
            RetainedAdapterRestartThreshold = Mathf.Clamp(RetainedAdapterRestartThreshold, 2, 32);
        }
    }
}
