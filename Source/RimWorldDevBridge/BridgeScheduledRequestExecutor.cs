using System;

namespace RimWorldDevBridge
{
    // Performs the second, owner-thread authorization and execution boundary for scheduled requests.
    internal sealed class BridgeScheduledRequestExecutor
    {
        private readonly BridgeAuthorization authorization;
        private readonly Func<string> sessionId;
        private readonly Func<bool> mutationEnabled;
        private readonly Func<BridgeRequest, bool> isCurrentTransport;
        private readonly Func<BridgeRequest, BridgeCommandDescriptor, BridgeResult> observeMutation;
        private readonly Func<BridgeResult, BridgeRequest, string, string, BridgeResult> decorate;

        internal BridgeScheduledRequestExecutor(BridgeAuthorization authorization, Func<string> sessionId,
            Func<bool> mutationEnabled, Func<BridgeRequest, bool> isCurrentTransport,
            Func<BridgeRequest, BridgeCommandDescriptor, BridgeResult> observeMutation,
            Func<BridgeResult, BridgeRequest, string, string, BridgeResult> decorate)
        {
            this.authorization = authorization;
            this.sessionId = sessionId;
            this.mutationEnabled = mutationEnabled;
            this.isCurrentTransport = isCurrentTransport;
            this.observeMutation = observeMutation;
            this.decorate = decorate;
        }

        internal BridgeResult Execute(BridgeRequest request)
        {
            if (!isCurrentTransport(request))
                return decorate(BridgeResult.Fail(BridgeStatus.INCOMPATIBLE, "stale_transport"), request,
                    "core", BridgeProtocol.BridgeVersion);
            BridgeCommandDescriptor descriptor = request.PreparedDescriptor ?? BridgeDispatch.Describe(request);
            if (descriptor == null) return decorate(BridgeResult.Fail(BridgeStatus.NOT_FOUND,
                "unknown_command"), request, "core", BridgeProtocol.BridgeVersion);
            if (request.SessionId != sessionId())
                return decorate(BridgeResult.Fail(BridgeStatus.INCOMPATIBLE, "stale_session"), request,
                    descriptor.Provider, descriptor.ProviderVersion);
            BridgeResult mutationGate = observeMutation(request, descriptor);
            if (mutationGate != null)
                return decorate(mutationGate, request, descriptor.Provider, descriptor.ProviderVersion);
            BridgeResult authorizationFailure = authorization.Authorize(request, descriptor, request.AuthToken,
                mutationEnabled());
            if (authorizationFailure != null)
                return decorate(authorizationFailure, request, descriptor.Provider, descriptor.ProviderVersion);
            if (authorization.TryGetCompleted(request, out BridgeResult cached))
                return decorate(cached, request, descriptor.Provider, descriptor.ProviderVersion);
            if (!isCurrentTransport(request) || request.SessionId != sessionId())
                return decorate(BridgeResult.Fail(BridgeStatus.INCOMPATIBLE, "stale_transport"), request,
                    descriptor.Provider, descriptor.ProviderVersion);
            if (descriptor.RequiresMap && BridgeGameState.CurrentMap == null)
                return decorate(BridgeResult.Fail(BridgeStatus.UNAVAILABLE, "map_required"), request,
                    descriptor.Provider, descriptor.ProviderVersion);
            if (request.Remaining.TotalMilliseconds < descriptor.MinimumExecutionBudgetMs)
                return decorate(BridgeResult.Fail(BridgeStatus.TIMEOUT, "insufficient_execution_budget")
                    .Add("requiredMs", descriptor.MinimumExecutionBudgetMs), request,
                    descriptor.Provider, descriptor.ProviderVersion);
            if (request.Expired || request.Cancelled || request.ClientDisconnected)
                return decorate(BridgeResult.Fail(request.Expired ? BridgeStatus.TIMEOUT : BridgeStatus.CANCELLED,
                    request.Expired ? "deadline_expired" : "cancelled_before_execution"), request,
                    descriptor.Provider, descriptor.ProviderVersion);
            if (!isCurrentTransport(request) || request.SessionId != sessionId())
                return decorate(BridgeResult.Fail(BridgeStatus.INCOMPATIBLE, "stale_transport"), request,
                    descriptor.Provider, descriptor.ProviderVersion);
            mutationGate = observeMutation(request, descriptor);
            if (mutationGate != null)
                return decorate(mutationGate, request, descriptor.Provider, descriptor.ProviderVersion);
            int tickBefore = BridgeGameState.TickManager?.TicksGame ?? -1;
            BridgeExecutionContext context = new BridgeExecutionContext(request, BridgeGameState.CurrentMap,
                () => request.Cancelled || request.ClientDisconnected);
            request.ExecutionReached = true;
            BridgeResult result = BridgeDispatch.Execute(context);
            bool staleTransport = !isCurrentTransport(request);
            if (request.Cancelled || request.ClientDisconnected || request.Expired ||
                request.SessionId != sessionId() || staleTransport)
            {
                return decorate(BridgeResult.Fail(request.Expired ? BridgeStatus.TIMEOUT :
                    (request.SessionId != sessionId() || staleTransport) ? BridgeStatus.INCOMPATIBLE :
                        BridgeStatus.CANCELLED,
                    request.Expired ? "execution_deadline_expired" :
                        staleTransport ? "stale_transport" :
                        request.SessionId != sessionId() ? "stale_session" : "execution_cancelled"), request,
                    descriptor.Provider, descriptor.ProviderVersion);
            }
            mutationGate = observeMutation(request, descriptor);
            if (mutationGate != null)
                return decorate(mutationGate, request, descriptor.Provider, descriptor.ProviderVersion);
            if (!isCurrentTransport(request) || request.SessionId != sessionId())
                return decorate(BridgeResult.Fail(BridgeStatus.INCOMPATIBLE, "stale_transport"), request,
                    descriptor.Provider, descriptor.ProviderVersion);
            if (result == null && request.YieldExecution) return null;
            if (result == null) result = BridgeResult.Fail(BridgeStatus.ERROR, "empty_result");
            result.TickBefore = tickBefore;
            result.TickAfter = BridgeGameState.TickManager?.TicksGame ?? -1;
            decorate(result, request, descriptor.Provider, descriptor.ProviderVersion);
            return result;
        }

        internal void Complete(BridgeRequest request, BridgeResult result)
        {
            BridgeCommandDescriptor descriptor = request.PreparedDescriptor ?? BridgeDispatch.Describe(request);
            if (descriptor == null) return;
            bool currentSession = string.Equals(request.SessionId, sessionId(), StringComparison.Ordinal) &&
                isCurrentTransport(request);
            if (!request.IdempotentReplay && currentSession)
            {
                if (request.ExecutionReached) authorization.Remember(request, result);
                authorization.Audit(request, result);
            }
            BridgeMetrics.Record(descriptor, result, request.AgentId);
            if (currentSession)
                BridgeEventJournal.Record("command", request.Command + " status:" + result.Status +
                    " provider:" + descriptor.Provider + " agent:" + BridgeText.Clean(request.AgentId ?? "anonymous") +
                    " executionMs:" + result.ExecutionMs.ToString("0.###"));
        }
    }
}
