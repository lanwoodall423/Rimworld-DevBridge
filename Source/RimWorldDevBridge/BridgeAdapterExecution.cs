using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Linq;

namespace RimWorldDevBridge
{
    // Executes a pinned adapter generation and owns invocation health and circuit policy.
    internal static class BridgeAdapterExecution
    {
        internal static BridgeResult Execute(BridgeExecutionContext context, object gate,
            IList<BridgeAdapterCatalog.AdapterGeneration> all,
            IDictionary<string, BridgeAdapterCatalog.AdapterGeneration> commandsByName,
            int circuitBreakFailures, double seriousOverrunMs)
        {
            BridgeAdapterCatalog.AdapterGeneration generation;
            lock (gate)
            {
                generation = context.Request.PreparedAdapter as BridgeAdapterCatalog.AdapterGeneration;
                if (generation == null) generation = all.FirstOrDefault(item =>
                    string.Equals(item.Manifest.adapterId, context.Request.PreparedAdapterId,
                        StringComparison.OrdinalIgnoreCase) &&
                    string.Equals(item.Manifest.generation, context.Request.PreparedAdapterGeneration,
                        StringComparison.OrdinalIgnoreCase));
                if (generation == null && !commandsByName.TryGetValue(context.Request.Command, out generation))
                    return null;
                if (generation.QuarantinedUntilUtc > DateTime.UtcNow)
                    return BridgeResult.Fail(BridgeStatus.UNAVAILABLE, "adapter_circuit_open");
            }
            try
            {
                BridgeAdapterLoader.EnsureLoaded(generation);
                long start = Stopwatch.GetTimestamp();
                BridgeResult result;
                bool cooperative = generation.TypedProvider is IBridgeCooperativeAdapterProvider &&
                    string.Equals(generation.Manifest.executionContract, "cooperative-v1",
                        StringComparison.OrdinalIgnoreCase);
                if (cooperative)
                {
                    IBridgeCooperativeAdapterExecution execution = context.Request.CooperativeState as
                        IBridgeCooperativeAdapterExecution;
                    if (execution == null)
                    {
                        execution = ((IBridgeCooperativeAdapterProvider)generation.TypedProvider)
                            .BeginCooperativeExecution(context);
                        if (execution == null) throw new InvalidOperationException(
                            "Cooperative adapter returned no execution state.");
                        context.Request.CooperativeState = execution;
                    }
                    result = execution.Step(context);
                    if (!execution.IsComplete)
                    {
                        context.Request.YieldExecution = true;
                        return null;
                    }
                    context.Request.CooperativeState = null;
                }
                else if (generation.TypedProvider != null)
                {
                    result = generation.TypedProvider.Execute(context);
                }
                else
                {
                    AdapterCommandManifest command = generation.Manifest.commands.First(item =>
                        item.name.Equals(context.Request.Command, StringComparison.OrdinalIgnoreCase));
                    object value = generation.LegacyExecute.Invoke(null,
                        new object[] { command.providerCommand ?? command.name,
                            context.Request.Argument ?? string.Empty, context.Map });
                    result = BridgeResult.FromLegacy(value as IEnumerable<string>);
                    if (context.Request.Mode != BridgeCommandMode.PureRead &&
                        string.Equals(result.MutationSummary, "none", StringComparison.Ordinal))
                        result.MutationSummary = "legacy adapter command completed; no detailed mutation summary supplied";
                    result.NonCooperativeExecution = true;
                }
                double elapsed = BridgeTiming.Milliseconds(start);
                double totalElapsed = context.Request.CooperativeExecutionMs + elapsed;
                result.ExecutionMs = totalElapsed;
                lock (gate)
                {
                    generation.InvocationCount++;
                    generation.TotalExecutionMs += totalElapsed;
                    generation.LastExecutionMs = totalElapsed;
                    generation.LastStatus = result.Status;
                    if (result.NonCooperativeExecution && totalElapsed >= seriousOverrunMs)
                    {
                        generation.SeriousOverruns++;
                        generation.State = "quarantined";
                        generation.QuarantinedUntilUtc = DateTime.UtcNow.AddMinutes(2);
                        generation.LastFailure = "non-cooperative overrun";
                    }
                    if (result.IsSuccess)
                    {
                        if (generation.QuarantinedUntilUtc <= DateTime.UtcNow)
                        {
                            generation.ConsecutiveFailures = 0;
                            generation.LastFailure = null;
                            generation.State = "loaded";
                        }
                    }
                    else
                    {
                        generation.FailureCount++;
                        generation.ConsecutiveFailures++;
                        generation.LastFailure = result.Status.ToString();
                        if (generation.ConsecutiveFailures >= circuitBreakFailures)
                        {
                            generation.State = "quarantined";
                            generation.QuarantinedUntilUtc = DateTime.UtcNow.AddMinutes(2);
                        }
                    }
                }
                if (result.NonCooperativeExecution)
                {
                    result.Warn("legacy adapter execution is synchronous and non-cooperative");
                    if (totalElapsed >= seriousOverrunMs)
                        result.Warn("legacy adapter circuit opened after serious overrun");
                }
                if (elapsed >= 50d) result.Warn("slow adapter command: " + elapsed.ToString("0.###") + " ms");
                return result;
            }
            catch (Exception exception)
            {
                Exception root = exception.GetBaseException();
                lock (gate)
                {
                    generation.InvocationCount++;
                    generation.FailureCount++;
                    generation.ConsecutiveFailures++;
                    generation.LastStatus = BridgeStatus.ERROR;
                    generation.LastFailure = root.GetType().Name + ": " + root.Message;
                    if (generation.ConsecutiveFailures >= circuitBreakFailures)
                    {
                        generation.State = "quarantined";
                        generation.QuarantinedUntilUtc = DateTime.UtcNow.AddMinutes(2);
                    }
                }
                return BridgeResult.Fail(BridgeStatus.ERROR, "adapter_failure", root.Message);
            }
        }
    }
}
