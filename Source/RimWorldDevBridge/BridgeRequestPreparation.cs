using System;
using System.Threading.Tasks;

namespace RimWorldDevBridge
{
    // Keeps request preparation on the owner thread without weakening deadline or stale-session checks.
    internal sealed class BridgeRequestPreparation
    {
        private readonly BridgeMainThreadContext mainThread;
        private readonly Func<string> sessionId;
        private readonly Func<BridgeRequest, bool> isCurrentTransport;
        private readonly Func<BridgeRequest, BridgeResult> coordinateRequest;
        private readonly Action<BridgeRequest, BridgeResult> coordinatedCompletion;

        internal BridgeRequestPreparation(BridgeMainThreadContext mainThread, Func<string> sessionId,
            Func<BridgeRequest, bool> isCurrentTransport,
            Func<BridgeRequest, BridgeResult> coordinateRequest = null,
            Action<BridgeRequest, BridgeResult> coordinatedCompletion = null)
        {
            this.mainThread = mainThread;
            this.sessionId = sessionId;
            this.isCurrentTransport = isCurrentTransport;
            this.coordinateRequest = coordinateRequest;
            this.coordinatedCompletion = coordinatedCompletion;
        }

        internal BridgePreparationResult Prepare(BridgeRequest request)
        {
            BridgeResult failure = PrepareOnMainThread(request, out BridgeCommandDescriptor descriptor);
            return new BridgePreparationResult(descriptor, failure);
        }

        private BridgeResult PrepareOnMainThread(BridgeRequest request,
            out BridgeCommandDescriptor descriptor)
        {
            descriptor = null;
            if (mainThread.IsOwnerThread) return PrepareRequest(request, out descriptor);

            TaskCompletionSource<BridgePreparationResult> completion =
                new TaskCompletionSource<BridgePreparationResult>();
            try
            {
                mainThread.Post(_ =>
                {
                    BridgeCommandDescriptor preparedDescriptor = null;
                    BridgeResult failure;
                    try
                    {
                        failure = request.SessionId != sessionId()
                            ? BridgeResult.Fail(BridgeStatus.INCOMPATIBLE, "stale_session")
                            : !isCurrentTransport(request)
                                ? BridgeResult.Fail(BridgeStatus.INCOMPATIBLE, "stale_transport")
                            : request.Expired || request.Cancelled
                                ? BridgeResult.Fail(request.Expired ? BridgeStatus.TIMEOUT : BridgeStatus.CANCELLED,
                                    request.Expired ? "deadline_expired" : "cancelled_before_prepare")
                            : PrepareRequest(request, out preparedDescriptor);
                    }
                    catch (Exception exception)
                    {
                        failure = BridgeResult.Fail(BridgeStatus.ERROR, "main_thread_prepare_failed",
                            exception.GetBaseException().Message);
                    }
                    completion.TrySetResult(new BridgePreparationResult(preparedDescriptor, failure));
                }, null);
            }
            catch (Exception exception)
            {
                return BridgeResult.Fail(BridgeStatus.ERROR, "main_thread_prepare_failed",
                    exception.GetBaseException().Message);
            }

            int waitMs = Math.Max(1, Math.Min(int.MaxValue,
                (int)Math.Ceiling(Math.Max(1d, request.Remaining.TotalMilliseconds))));
            if (!completion.Task.Wait(waitMs))
            {
                request.Cancelled = true;
                return BridgeResult.Fail(BridgeStatus.TIMEOUT, "main_thread_prepare_timeout");
            }
            BridgePreparationResult result = completion.Task.Result;
            descriptor = result.Descriptor;
            return result.Failure;
        }

        private BridgeResult PrepareRequest(BridgeRequest request,
            out BridgeCommandDescriptor descriptor)
        {
            if (request.Expired)
            {
                descriptor = null;
                return BridgeResult.Fail(BridgeStatus.TIMEOUT, "deadline_expired");
            }
            descriptor = BridgeDispatch.Describe(request);
            if (descriptor == null) return null;
            request.Mode = descriptor.Mode;
            request.Cost = descriptor.Cost;
            request.PreparedDescriptor = descriptor.Clone();
            BridgeResult coordinationFailure = coordinateRequest?.Invoke(request);
            if (coordinationFailure != null)
            {
                if (request.SharedOperationRegistered) coordinatedCompletion?.Invoke(request, coordinationFailure);
                return coordinationFailure;
            }
            BridgeResult result = BridgeDispatch.Prepare(request);
            if (result != null && request.SharedOperationRegistered)
                coordinatedCompletion?.Invoke(request, result);
            return result;
        }
    }
}
