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
            BridgeRuntime.OnFinalizeInit();
        }
    }
}
