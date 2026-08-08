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
                CorrelationId = parent.CorrelationId,
                AgentId = parent.AgentId,
                ClientInstanceId = parent.ClientInstanceId,
                ClientCredential = parent.ClientCredential,
                ParticipantId = parent.ParticipantId,
                ConnectionSessionId = parent.ConnectionSessionId,
                WorkspaceId = parent.WorkspaceId,
                SessionId = parent.SessionId,
                Command = BridgeText.NormalizeCommand(command),
                OperationId = parent.OperationId,
                OperationKind = parent.OperationKind,
                DesiredState = parent.DesiredState,
                CompatibilityKey = parent.CompatibilityKey,
                ManagedProfile = parent.ManagedProfile,
                RimWorldVersion = parent.RimWorldVersion,
                ModSetFingerprint = parent.ModSetFingerprint,
                ModLoadOrderFingerprint = parent.ModLoadOrderFingerprint,
                SourceBuildIdentity = parent.SourceBuildIdentity,
                ExpectedCoreFingerprint = parent.ExpectedCoreFingerprint,
                ExpectedAdapterFingerprint = parent.ExpectedAdapterFingerprint,
                ExpectedLoadedAssemblyFingerprint = parent.ExpectedLoadedAssemblyFingerprint,
                ConfigurationFingerprint = parent.ConfigurationFingerprint,
                UserRootFingerprint = parent.UserRootFingerprint,
                SaveTarget = parent.SaveTarget,
                MapTarget = parent.MapTarget,
                RequiresProcessReplacement = parent.RequiresProcessReplacement,
                KeepRunning = parent.KeepRunning,
                LifecycleGeneration = parent.LifecycleGeneration,
                MutationScope = parent.MutationScope,
                RuntimeSlotId = parent.RuntimeSlotId,
                DeploymentId = parent.DeploymentId,
                ArtifactFingerprint = parent.ArtifactFingerprint,
                ExpectedProcessId = parent.ExpectedProcessId,
                ExpectedProcessStartIdentity = parent.ExpectedProcessStartIdentity,
                ExpectedProcessSessionId = parent.ExpectedProcessSessionId,
                ExpectedProcessLifecycleGeneration = parent.ExpectedProcessLifecycleGeneration,
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
                CorrelationId = source.CorrelationId,
                AgentId = source.AgentId,
                ClientInstanceId = source.ClientInstanceId,
                ClientCredential = source.ClientCredential,
                ParticipantId = source.ParticipantId,
                ConnectionSessionId = source.ConnectionSessionId,
                WorkspaceId = source.WorkspaceId,
                SessionId = source.SessionId,
                Command = source.Command,
                Argument = source.Argument,
                OperationId = source.OperationId,
                OperationKind = source.OperationKind,
                DesiredState = source.DesiredState,
                CompatibilityKey = source.CompatibilityKey,
                ManagedProfile = source.ManagedProfile,
                RimWorldVersion = source.RimWorldVersion,
                ModSetFingerprint = source.ModSetFingerprint,
                ModLoadOrderFingerprint = source.ModLoadOrderFingerprint,
                SourceBuildIdentity = source.SourceBuildIdentity,
                ExpectedCoreFingerprint = source.ExpectedCoreFingerprint,
                ExpectedAdapterFingerprint = source.ExpectedAdapterFingerprint,
                ExpectedLoadedAssemblyFingerprint = source.ExpectedLoadedAssemblyFingerprint,
                ConfigurationFingerprint = source.ConfigurationFingerprint,
                UserRootFingerprint = source.UserRootFingerprint,
                SaveTarget = source.SaveTarget,
                MapTarget = source.MapTarget,
                RequiresProcessReplacement = source.RequiresProcessReplacement,
                KeepRunning = source.KeepRunning,
                LifecycleGeneration = source.LifecycleGeneration,
                MutationScope = source.MutationScope,
                RuntimeSlotId = source.RuntimeSlotId,
                DeploymentId = source.DeploymentId,
                ArtifactFingerprint = source.ArtifactFingerprint,
                ExpectedProcessId = source.ExpectedProcessId,
                ExpectedProcessStartIdentity = source.ExpectedProcessStartIdentity,
                ExpectedProcessSessionId = source.ExpectedProcessSessionId,
                ExpectedProcessLifecycleGeneration = source.ExpectedProcessLifecycleGeneration,
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
            result.CorrelationId = call.Request.CorrelationId;
            result.AgentId = call.Request.AgentId;
            result.ClientInstanceId = call.Request.ClientInstanceId;
            result.ParticipantId = call.Request.ParticipantId;
            result.SessionId = call.Request.SessionId;
            result.ConnectionSessionId = call.Request.ConnectionSessionId;
            result.Command = call.Request.Command;
            result.OperationId = call.Request.OperationId;
            result.OperationKind = call.Request.OperationKind;
            result.DesiredState = call.Request.DesiredState;
            result.CompatibilityKey = call.Request.CompatibilityKey;
            result.RuntimeSlotId = call.Request.RuntimeSlotId;
            result.DeploymentId = call.Request.DeploymentId;
            result.ArtifactFingerprint = call.Request.ArtifactFingerprint;
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
