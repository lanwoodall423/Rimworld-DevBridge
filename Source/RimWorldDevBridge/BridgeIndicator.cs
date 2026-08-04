using System;
using System.Linq;
using UnityEngine;
using Verse;

namespace RimWorldDevBridge
{
    internal enum BridgeIndicatorMode
    {
        Hidden,
        ReadOnly,
        Sandbox,
        LiveConfirmed
    }

    internal sealed class BridgeIndicatorState
    {
        internal readonly BridgeIndicatorMode Mode;
        internal readonly bool Visible;
        internal readonly bool TransportActive;
        internal readonly int ConnectedClients;
        internal readonly int ConnectedClientLimit;
        internal readonly string Context;
        internal readonly DateTime? LeaseExpiresUtc;

        private BridgeIndicatorState(BridgeIndicatorMode mode, bool visible, bool transportActive,
            int connectedClients, int connectedClientLimit, string context, DateTime? leaseExpiresUtc)
        {
            Mode = mode;
            Visible = visible;
            TransportActive = transportActive;
            ConnectedClients = connectedClients;
            ConnectedClientLimit = connectedClientLimit;
            Context = context;
            LeaseExpiresUtc = leaseExpiresUtc;
        }

        internal static BridgeIndicatorState Create(bool transportActive, int connectedClients,
            int connectedClientLimit, BridgeSessionContextSnapshot session, bool settingVisible)
        {
            bool leaseActive = session != null && session.WriteLeaseActive;
            BridgeIndicatorMode mode = !leaseActive ?
                (transportActive || settingVisible ? BridgeIndicatorMode.ReadOnly : BridgeIndicatorMode.Hidden) :
                string.Equals(session.WriteContext, "live-confirmed", StringComparison.OrdinalIgnoreCase)
                    ? BridgeIndicatorMode.LiveConfirmed : BridgeIndicatorMode.Sandbox;
            // The preference controls the optional idle read-only display. Active transport and every
            // write lease always remain visible so dangerous access cannot be hidden.
            bool visible = settingVisible || transportActive || leaseActive;
            return new BridgeIndicatorState(mode, visible, transportActive,
                Math.Max(0, connectedClients), Math.Max(0, connectedClientLimit),
                session?.WriteContext ?? "none", session?.LeaseExpiresUtc);
        }

        internal string Label
        {
            get
            {
                switch (Mode)
                {
                    case BridgeIndicatorMode.LiveConfirmed: return "DEV BRIDGE  |  LIVE-CONFIRMED WRITE";
                    case BridgeIndicatorMode.Sandbox: return "DEV BRIDGE  |  SANDBOX WRITE";
                    default: return "DEV BRIDGE  |  READ ONLY";
                }
            }
        }

        internal string CompactDetails(DateTime utcNow)
        {
            string transport = TransportActive ? "active" : "idle";
            string lease = Context == "none" ? "none" : Context;
            string expiry = LeaseExpiresUtc.HasValue
                ? " expires:" + Math.Max(0d, (LeaseExpiresUtc.Value - utcNow).TotalSeconds).ToString("0") + "s"
                : string.Empty;
            return transport + "  clients:" + ConnectedClients + "/" + ConnectedClientLimit +
                "  lease:" + lease + expiry;
        }

        internal string Tooltip(DateTime utcNow)
        {
            string warning = Mode == BridgeIndicatorMode.LiveConfirmed
                ? "\nLIVE-CONFIRMED writes are enabled."
                : string.Empty;
            return Label + "\n" + CompactDetails(utcNow) + warning +
                "\nThe indicator remains visible while write access is leased.";
        }
    }

    internal static class BridgeIndicator
    {
        private static BridgeIndicatorState state = BridgeIndicatorState.Create(false, 0, 0, null, false);
        private static BridgeIndicatorWindow window;

        internal static BridgeIndicatorState State => state;
        internal static int RefreshCountForTests => System.Threading.Volatile.Read(ref refreshCount);
        internal static void ResetRefreshCountForTests() =>
            System.Threading.Interlocked.Exchange(ref refreshCount, 0);

        internal static void Refresh(BridgeRuntime.BridgeRuntimeStateSnapshot snapshot,
            bool settingVisible, int corner)
        {
            if (snapshot == null) return;
            Refresh(snapshot.TransportActive, snapshot.ConnectedClients,
                snapshot.ConnectedClientLimit, snapshot.Context, settingVisible, corner);
        }

        internal static void Refresh(bool transportActive, int connectedClients, int connectedClientLimit,
            BridgeSessionContextSnapshot session, bool settingVisible, int corner)
        {
            System.Threading.Interlocked.Increment(ref refreshCount);
            BridgeIndicatorState next = BridgeIndicatorState.Create(transportActive, connectedClients,
                connectedClientLimit, session, settingVisible);
            state = next;
            if (!next.Visible)
            {
                CloseWindow();
                return;
            }

            if (Find.WindowStack == null) return;
            if (window == null || !Find.WindowStack.Windows.Contains(window))
            {
                window = new BridgeIndicatorWindow();
                Find.WindowStack.Add(window);
            }
            window.SetState(next, corner);
        }

        internal static void Close()
        {
            state = BridgeIndicatorState.Create(false, 0, 0, null, false);
            CloseWindow();
        }

        private static int refreshCount;

        private static void CloseWindow()
        {
            if (window == null) return;
            try { window.Close(false); } catch { }
            window = null;
        }
    }

    internal sealed class BridgeIndicatorWindow : Window
    {
        private BridgeIndicatorState state;
        private int corner;

        internal BridgeIndicatorWindow()
        {
            doCloseX = false;
            doCloseButton = false;
            closeOnAccept = false;
            closeOnCancel = false;
            closeOnClickedOutside = false;
            absorbInputAroundWindow = false;
            forcePause = false;
            draggable = false;
            resizeable = false;
            preventCameraMotion = false;
        }

        public override Vector2 InitialSize => new Vector2(355f,
            state != null && state.Mode == BridgeIndicatorMode.LiveConfirmed ? 56f : 46f);

        internal void SetState(BridgeIndicatorState next, int nextCorner)
        {
            state = next;
            corner = Mathf.Clamp(nextCorner, 0, 3);
            SetPosition();
        }

        public override void DoWindowContents(Rect inRect)
        {
            if (state == null) return;
            Color previousColor = GUI.color;
            GameFont previousFont = Text.Font;
            Color background = state.Mode == BridgeIndicatorMode.LiveConfirmed
                ? new Color(0.55f, 0.03f, 0.02f, 0.96f)
                : state.Mode == BridgeIndicatorMode.Sandbox
                    ? new Color(0.50f, 0.28f, 0.02f, 0.94f)
                    : new Color(0.05f, 0.18f, 0.28f, 0.90f);
            Widgets.DrawBoxSolid(new Rect(0f, 0f, inRect.width, inRect.height), background);
            Text.Font = GameFont.Tiny;
            GUI.color = Color.white;
            Widgets.Label(new Rect(8f, 4f, inRect.width - 16f, 18f), state.Label);
            Widgets.Label(new Rect(8f, 22f, inRect.width - 16f, 18f),
                state.CompactDetails(DateTime.UtcNow));
            TooltipHandler.TipRegion(new Rect(0f, 0f, inRect.width, inRect.height),
                state.Tooltip(DateTime.UtcNow));
            Text.Font = previousFont;
            GUI.color = previousColor;
        }

        private void SetPosition()
        {
            float margin = 8f;
            float width = InitialSize.x;
            float height = InitialSize.y;
            float x = corner == 1 || corner == 3 ? margin : UI.screenWidth - width - margin;
            float y = corner == 2 || corner == 3 ? UI.screenHeight - height - margin : margin;
            windowRect = new Rect(Mathf.Max(margin, x), Mathf.Max(margin, y), width, height);
        }
    }
}
