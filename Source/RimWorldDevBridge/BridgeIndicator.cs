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
        internal readonly BridgeMutationConfirmationSnapshot Confirmation;

        private BridgeIndicatorState(BridgeIndicatorMode mode, bool visible, bool transportActive,
            int connectedClients, int connectedClientLimit, string context, DateTime? leaseExpiresUtc,
            BridgeMutationConfirmationSnapshot confirmation)
        {
            Mode = mode;
            Visible = visible;
            TransportActive = transportActive;
            ConnectedClients = connectedClients;
            ConnectedClientLimit = connectedClientLimit;
            Context = context;
            LeaseExpiresUtc = leaseExpiresUtc;
            Confirmation = confirmation;
        }

        internal static BridgeIndicatorState Create(bool transportActive, int connectedClients,
            int connectedClientLimit, BridgeSessionContextSnapshot session, bool settingVisible)
        {
            return Create(transportActive, connectedClients, connectedClientLimit, session, settingVisible, null);
        }

        internal static BridgeIndicatorState Create(bool transportActive, int connectedClients,
            int connectedClientLimit, BridgeSessionContextSnapshot session, bool settingVisible,
            BridgeMutationConfirmationSnapshot confirmation)
        {
            bool leaseActive = session != null && session.WriteLeaseActive;
            BridgeIndicatorMode mode = !leaseActive ?
                (transportActive || settingVisible ? BridgeIndicatorMode.ReadOnly : BridgeIndicatorMode.Hidden) :
                string.Equals(session.WriteContext, "live-confirmed", StringComparison.OrdinalIgnoreCase)
                    ? BridgeIndicatorMode.LiveConfirmed : BridgeIndicatorMode.Sandbox;
            // The preference controls the optional idle read-only display. Active transport and every
            // write lease always remain visible so dangerous access cannot be hidden.
            bool visible = settingVisible || transportActive || leaseActive ||
                (confirmation != null && confirmation.Visible);
            return new BridgeIndicatorState(mode, visible, transportActive,
                Math.Max(0, connectedClients), Math.Max(0, connectedClientLimit),
                session?.WriteContext ?? "none", session?.LeaseExpiresUtc, confirmation);
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
            string confirmation = Confirmation == null ? string.Empty :
                "  confirmation:" + Confirmation.State;
            return transport + "  clients:" + ConnectedClients + "/" + ConnectedClientLimit +
                "  lease:" + lease + expiry + confirmation;
        }

        internal string Tooltip(DateTime utcNow)
        {
            string warning = Mode == BridgeIndicatorMode.LiveConfirmed
                ? "\nLIVE-CONFIRMED writes are enabled."
                : Confirmation != null && Confirmation.Visible
                    ? "\nWARNING: Remote tools may modify or destroy game state."
                    : string.Empty;
            return Label + "\n" + CompactDetails(utcNow) + warning +
                (Confirmation != null && Confirmation.Visible
                    ? "\nIn-game confirmation is required and can be revoked here."
                    : "\nThe indicator remains visible while write access is leased.");
        }
    }

    internal static class BridgeIndicator
    {
        private static BridgeIndicatorState state = BridgeIndicatorState.Create(false, 0, 0, null, false);
        private static BridgeIndicatorWindow window;
        private static BridgeMutationConfirmationDialog confirmationDialog;
        private static readonly BridgeMutationConfirmationPrompt confirmationPrompt =
            new BridgeMutationConfirmationPrompt();

        internal static BridgeIndicatorState State => state;
        internal static int RefreshCountForTests => System.Threading.Volatile.Read(ref refreshCount);
        internal static void ResetRefreshCountForTests() =>
            System.Threading.Interlocked.Exchange(ref refreshCount, 0);

        internal static void Refresh(BridgeRuntime.BridgeRuntimeStateSnapshot snapshot,
            bool settingVisible, int corner)
        {
            if (snapshot == null) return;
            Refresh(snapshot.TransportActive, snapshot.ConnectedClients,
                snapshot.ConnectedClientLimit, snapshot.Context, settingVisible, corner,
                snapshot.MutationConfirmation);
        }

        internal static void Refresh(bool transportActive, int connectedClients, int connectedClientLimit,
            BridgeSessionContextSnapshot session, bool settingVisible, int corner)
        {
            Refresh(transportActive, connectedClients, connectedClientLimit, session, settingVisible, corner, null);
        }

        private static void Refresh(bool transportActive, int connectedClients, int connectedClientLimit,
            BridgeSessionContextSnapshot session, bool settingVisible, int corner,
            BridgeMutationConfirmationSnapshot confirmation)
        {
            System.Threading.Interlocked.Increment(ref refreshCount);
            BridgeIndicatorState next = BridgeIndicatorState.Create(transportActive, connectedClients,
                connectedClientLimit, session, settingVisible, confirmation);
            state = next;
            if (next.Confirmation == null || !next.Confirmation.Visible || next.Confirmation.Confirmed)
                CloseConfirmationDialog();
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
            state = BridgeIndicatorState.Create(false, 0, 0, null, false, null);
            CloseConfirmationDialog();
            CloseWindow();
        }

        internal static void OpenConfirmationDialog()
        {
            if (confirmationDialog != null || !confirmationPrompt.BeginSecondStage(state.Confirmation)) return;
            if (Find.WindowStack == null)
            {
                confirmationPrompt.CancelSecondStage();
                return;
            }
            confirmationDialog = new BridgeMutationConfirmationDialog(
                CancelConfirmationDialog,
                ConfirmConfirmationDialog);
            Find.WindowStack.Add(confirmationDialog);
        }

        internal static void CancelConfirmationReview()
        {
            confirmationPrompt.CancelFirstStage();
            CloseConfirmationDialog();
        }

        private static int refreshCount;

        private static void CloseWindow()
        {
            if (window == null) return;
            try { window.Close(false); } catch { }
            window = null;
        }

        private static void CancelConfirmationDialog()
        {
            confirmationPrompt.CancelSecondStage();
            CloseConfirmationDialog();
        }

        private static void ConfirmConfirmationDialog()
        {
            confirmationPrompt.ConfirmSecondStage(() => BridgeRuntime.ConfirmMutationForCurrentGame());
            CloseConfirmationDialog();
        }

        private static void CloseConfirmationDialog()
        {
            confirmationPrompt.Reset();
            if (confirmationDialog == null) return;
            try { confirmationDialog.Close(false); } catch { }
            confirmationDialog = null;
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
            state != null && state.Confirmation != null && state.Confirmation.Visible ? 126f :
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
            if (state.Confirmation != null && state.Confirmation.Visible)
            {
                Text.Font = GameFont.Tiny;
                GUI.color = new Color(1f, 0.88f, 0.35f);
                Widgets.Label(new Rect(8f, 42f, inRect.width - 16f, 18f),
                    BridgeMutationConfirmation.Warning);
                Text.Font = GameFont.Tiny;
                GUI.color = Color.white;
                if (state.Confirmation.Confirmed)
                {
                    Widgets.Label(new Rect(8f, 62f, inRect.width - 16f, 18f),
                        "REMOTE WRITES CONFIRMED FOR THIS GAME");
                    Rect buttonRect = new Rect(8f, 88f, inRect.width - 16f, 26f);
                    if (Widgets.ButtonText(buttonRect, "Revoke remote mutation confirmation"))
                        BridgeRuntime.RevokeMutationConfirmation();
                }
                else
                {
                    Widgets.Label(new Rect(8f, 62f, inRect.width - 16f, 18f),
                        "REMOTE WRITES REQUIRE IN-GAME CONFIRMATION");
                    Rect reviewRect = new Rect(8f, 88f, 224f, 26f);
                    Rect cancelRect = new Rect(240f, 88f, inRect.width - 248f, 26f);
                    if (Widgets.ButtonText(reviewRect, "Review warning"))
                        BridgeIndicator.OpenConfirmationDialog();
                    if (Widgets.ButtonText(cancelRect, "Cancel"))
                        BridgeIndicator.CancelConfirmationReview();
                }
            }
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
