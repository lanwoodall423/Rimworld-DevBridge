using System;
using System.Collections.Generic;
using System.Linq;

namespace RimWorldDevBridge
{
    internal static class BridgeDispatch
    {
        internal static BridgeCommandDescriptor Describe(BridgeRequest request)
        {
            if (request == null) return null;
            return BridgeCommands.Describe(request.Command) ?? BridgeAdapterCatalog.Describe(request.Command) ??
                BridgeFeatureTests.Describe(request) ??
                BridgeOrchestration.Describe(request.Command, request.Argument);
        }

        internal static BridgeResult Prepare(BridgeRequest request)
        {
            if (BridgeCommands.Describe(request.Command) != null) return BridgeCommands.Prepare(request);
            if (BridgeAdapterCatalog.Describe(request.Command) != null)
                return BridgeAdapterCatalog.Prepare(request);
            if (BridgeFeatureTests.Describe(request) != null)
                return BridgeFeatureTests.Prepare(request);
            if (BridgeOrchestration.Describe(request.Command, request.Argument) != null)
                return BridgeOrchestration.Prepare(request);
            return null;
        }

        internal static BridgeResult Execute(BridgeExecutionContext context)
        {
            return BridgeCommands.Execute(context) ?? BridgeAdapterCatalog.Execute(context) ??
                BridgeFeatureTests.Execute(context) ?? BridgeOrchestration.Execute(context);
        }

        internal static BridgeResult PrepareChild(BridgeRequest parent, string command, string argument,
            out PreparedCall call)
        {
            call = null;
            BridgeRequest child = new BridgeRequest
            {
                RequestId = parent.RequestId,
                SessionId = parent.SessionId,
                Command = BridgeText.NormalizeCommand(command),
                Argument = argument ?? string.Empty,
                EnqueuedUtc = parent.EnqueuedUtc,
                ReceivedUtc = parent.ReceivedUtc,
                DeadlineUtc = parent.DeadlineUtc,
                OutputFormat = parent.OutputFormat,
                DetailLevel = parent.DetailLevel,
                AllowExpensive = parent.AllowExpensive,
                AuthToken = parent.AuthToken,
                TransportGeneration = parent.TransportGeneration,
                IdempotencyKey = parent.IdempotencyKey,
                NestingDepth = parent.NestingDepth + 1
            };
            BridgeCommandDescriptor descriptor = Describe(child);
            if (descriptor == null) return BridgeResult.Fail(BridgeStatus.NOT_FOUND, "nested_command_not_found", child.Command);
            child.Mode = descriptor.Mode;
            child.Cost = descriptor.Cost;
            child.PreparedDescriptor = descriptor.Clone();
            BridgeResult failure = Prepare(child);
            if (failure != null) return failure;
            call = new PreparedCall { Request = child, Descriptor = descriptor };
            return null;
        }

        internal static BridgeResult ExecuteChild(BridgeExecutionContext parent, PreparedCall call)
        {
            parent.ThrowIfCancellationRequested();
            if (parent.DeadlineUtc < call.Request.DeadlineUtc)
                call.Request.DeadlineUtc = parent.DeadlineUtc;
            if (call.Descriptor.RequiresMap && parent.Map == null)
                return BridgeResult.Fail(BridgeStatus.UNAVAILABLE, "map_required");
            BridgeExecutionContext context = new BridgeExecutionContext(call.Request, parent.Map,
                () => parent.IsCancellationRequested);
            return DecorateChild(Execute(context) ?? BridgeResult.Fail(BridgeStatus.ERROR,
                "empty_nested_result"), call);
        }

        internal static BridgeResult ExecuteCleanupChild(BridgeExecutionContext parent, PreparedCall call)
        {
            BridgeRequest source = call.Request;
            BridgeRequest cleanup = new BridgeRequest
            {
                RequestId = source.RequestId,
                SessionId = source.SessionId,
                Command = source.Command,
                Argument = source.Argument,
                EnqueuedUtc = source.EnqueuedUtc,
                ReceivedUtc = source.ReceivedUtc,
                DeadlineUtc = DateTime.UtcNow.AddSeconds(2),
                OutputFormat = source.OutputFormat,
                DetailLevel = source.DetailLevel,
                AllowExpensive = source.AllowExpensive,
                Mode = source.Mode,
                Cost = source.Cost,
                AuthToken = source.AuthToken,
                TransportGeneration = source.TransportGeneration,
                IdempotencyKey = source.IdempotencyKey,
                PreparedAdapterId = source.PreparedAdapterId,
                PreparedAdapterGeneration = source.PreparedAdapterGeneration,
                PreparedAdapter = source.PreparedAdapter,
                PreparedDescriptor = source.PreparedDescriptor?.Clone(),
                PreparedPayload = source.PreparedPayload,
                NestingDepth = source.NestingDepth
            };
            BridgeExecutionContext context = new BridgeExecutionContext(cleanup, parent.Map, () => false);
            return DecorateChild(Execute(context) ?? BridgeResult.Fail(BridgeStatus.ERROR,
                "empty_cleanup_result"), call);
        }

        private static BridgeResult DecorateChild(BridgeResult result, PreparedCall call)
        {
            result.RequestId = call.Request.RequestId;
            result.SessionId = call.Request.SessionId;
            result.Command = call.Request.Command;
            result.Provider = call.Descriptor.Provider ?? "core";
            result.ProviderVersion = call.Descriptor.ProviderVersion ?? BridgeProtocol.BridgeVersion;
            result.Mode = call.Descriptor.Mode;
            return result;
        }

        internal static BridgeCommandMode MaximumMode(IEnumerable<PreparedCall> calls) =>
            calls?.Select(call => call.Descriptor.Mode).DefaultIfEmpty(BridgeCommandMode.PureRead).Max() ??
            BridgeCommandMode.PureRead;

        internal static BridgeCostClass MaximumCost(IEnumerable<PreparedCall> calls) =>
            calls?.Select(call => call.Descriptor.Cost).DefaultIfEmpty(BridgeCostClass.Trivial).Max() ??
            BridgeCostClass.Trivial;
    }

    internal sealed class PreparedCall
    {
        internal BridgeRequest Request;
        internal BridgeCommandDescriptor Descriptor;
    }
}
