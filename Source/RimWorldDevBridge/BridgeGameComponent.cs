using Verse;

namespace RimWorldDevBridge
{
    public sealed class BridgeGameComponent : GameComponent
    {
        public BridgeGameComponent(Game game)
        {
        }

        public override void FinalizeInit()
        {
            // Verse may invoke this during a loading long event. BridgeRuntime defers all
            // game-dependent finalization until Root.Update establishes the owner thread.
            BridgeRuntime.OnFinalizeInit();
        }
    }
}
