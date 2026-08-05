using System;
using System.Collections.Generic;
using System.Linq;

namespace RimWorldDevBridge
{
    internal static class BridgeMetrics
    {
        private static readonly object Gate = new object();
        private static readonly Dictionary<string, CommandMetric> Values =
            new Dictionary<string, CommandMetric>(StringComparer.OrdinalIgnoreCase);

        internal static void Record(BridgeCommandDescriptor descriptor, BridgeResult result, string agentId = null)
        {
            lock (Gate)
            {
                if (!Values.TryGetValue(descriptor.Name, out CommandMetric metric))
                    Values[descriptor.Name] = metric = new CommandMetric();
                metric.Count++;
                metric.TotalMs += result.ExecutionMs;
                metric.MaxMs = Math.Max(metric.MaxMs, result.ExecutionMs);
                metric.MaxStepMs = Math.Max(metric.MaxStepMs, result.MaxMainThreadStepMs);
                if (result.MainThreadOverrun) metric.Overruns++;
                metric.CooperativeSteps += result.CooperativeSteps;
                if (descriptor.NonCooperative || result.NonCooperativeExecution) metric.NonCooperative = true;
                if (descriptor.Cooperative) metric.Cooperative = true;
                metric.LastStatus = result.Status;
                if (metric.Agents.Count < 64 && !string.IsNullOrWhiteSpace(agentId)) metric.Agents.Add(agentId);
                if (!result.IsSuccess) metric.Failures++;
            }
        }

        internal static BridgeResult Report()
        {
            BridgeResult result = BridgeResult.Ok("core.commandMetrics");
            lock (Gate)
            {
                foreach (KeyValuePair<string, CommandMetric> pair in Values.OrderByDescending(value => value.Value.MaxMs))
                    result.AddLine("command=" + pair.Key + " calls:" + pair.Value.Count + " failures:" +
                        pair.Value.Failures + " meanMs:" + (pair.Value.Count > 0 ? pair.Value.TotalMs / pair.Value.Count : 0d)
                            .ToString("0.###", System.Globalization.CultureInfo.InvariantCulture) + " maxMs:" +
                        pair.Value.MaxMs.ToString("0.###", System.Globalization.CultureInfo.InvariantCulture) +
                        " maxStepMs:" + pair.Value.MaxStepMs.ToString("0.###", System.Globalization.CultureInfo.InvariantCulture) +
                        " overruns:" + pair.Value.Overruns + " cooperativeSteps:" + pair.Value.CooperativeSteps +
                        " agents:" + pair.Value.Agents.Count +
                        " contract:" + (pair.Value.NonCooperative ? "legacy-sync-non-cooperative" :
                            pair.Value.Cooperative ? "cooperative-v1" : "sync") +
                        " last:" + pair.Value.LastStatus);
            }
            return result;
        }

        private sealed class CommandMetric
        {
            internal long Count;
            internal long Failures;
            internal double TotalMs;
            internal double MaxMs;
            internal double MaxStepMs;
            internal long Overruns;
            internal long CooperativeSteps;
            internal bool NonCooperative;
            internal bool Cooperative;
            internal BridgeStatus LastStatus;
            internal readonly HashSet<string> Agents = new HashSet<string>(StringComparer.Ordinal);
        }
    }
}
