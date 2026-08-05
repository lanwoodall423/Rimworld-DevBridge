using System;
using UnityEngine;
using Verse;

namespace RimWorldDevBridge
{
    // UI-only state for the two deliberate confirmation stages. It never grants authority itself;
    // the supplied action is called only after the second-stage Confirm button is pressed.
    internal sealed class BridgeMutationConfirmationPrompt
    {
        private bool awaitingSecondConfirmation;

        internal bool IsAwaitingSecondConfirmation => awaitingSecondConfirmation;

        internal bool BeginSecondStage(BridgeMutationConfirmationSnapshot confirmation)
        {
            if (confirmation == null || !confirmation.Visible || confirmation.Confirmed) return false;
            awaitingSecondConfirmation = true;
            return true;
        }

        internal void CancelFirstStage()
        {
            awaitingSecondConfirmation = false;
        }

        internal void CancelSecondStage()
        {
            awaitingSecondConfirmation = false;
        }

        internal bool ConfirmSecondStage(Func<BridgeResult> confirmAction)
        {
            if (!awaitingSecondConfirmation) return false;
            awaitingSecondConfirmation = false;
            if (confirmAction != null) confirmAction();
            return true;
        }

        internal void Reset()
        {
            awaitingSecondConfirmation = false;
        }
    }

    // Runtime-only dialog. The explicit buttons make cancellation observable and keep the
    // authority-changing callback behind the second confirmation stage.
    internal sealed class BridgeMutationConfirmationDialog : Window
    {
        private readonly Action cancelAction;
        private readonly Action confirmAction;

        internal BridgeMutationConfirmationDialog(Action cancelAction, Action confirmAction)
        {
            this.cancelAction = cancelAction;
            this.confirmAction = confirmAction;
            doCloseX = false;
            doCloseButton = false;
            closeOnAccept = false;
            closeOnCancel = false;
            closeOnClickedOutside = false;
            absorbInputAroundWindow = true;
            forcePause = false;
            draggable = false;
            resizeable = false;
            preventCameraMotion = true;
        }

        public override Vector2 InitialSize => new Vector2(520f, 180f);

        public override void DoWindowContents(Rect inRect)
        {
            Color previousColor = GUI.color;
            GameFont previousFont = Text.Font;
            Widgets.DrawBoxSolid(new Rect(0f, 0f, inRect.width, inRect.height),
                new Color(0.34f, 0.02f, 0.01f, 0.98f));
            Text.Font = GameFont.Small;
            GUI.color = Color.white;
            Widgets.Label(new Rect(12f, 12f, inRect.width - 24f, 28f),
                "Confirm remote mutation access");
            Text.Font = GameFont.Tiny;
            GUI.color = new Color(1f, 0.88f, 0.35f);
            Widgets.Label(new Rect(12f, 46f, inRect.width - 24f, 38f),
                BridgeMutationConfirmation.Warning +
                " This is the second confirmation. Cancel leaves remote mutation disabled.");
            GUI.color = Color.white;
            Rect cancelRect = new Rect(12f, inRect.height - 42f, (inRect.width - 36f) / 2f, 30f);
            Rect confirmRect = new Rect(cancelRect.xMax + 12f, cancelRect.y, cancelRect.width, 30f);
            if (Widgets.ButtonText(cancelRect, "Cancel"))
            {
                cancelAction?.Invoke();
                Close(false);
            }
            if (Widgets.ButtonText(confirmRect, "Confirm"))
            {
                confirmAction?.Invoke();
                Close(false);
            }
            Text.Font = previousFont;
            GUI.color = previousColor;
        }
    }
}
