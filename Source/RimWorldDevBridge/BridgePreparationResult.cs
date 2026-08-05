namespace RimWorldDevBridge
{
    internal sealed class BridgePreparationResult
    {
        internal readonly BridgeCommandDescriptor Descriptor;
        internal readonly BridgeResult Failure;

        internal BridgePreparationResult(BridgeCommandDescriptor descriptor, BridgeResult failure)
        {
            Descriptor = descriptor;
            Failure = failure;
        }
    }
}
