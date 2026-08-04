using System;
using System.Collections.Generic;
using System.Threading;
using RimWorldDevBridge;

namespace BridgeFixtureAdapter
{
    public static class LegacySlowProvider
    {
        public static IEnumerable<string> ExecuteBridgeCommand(string command, string argument, Verse.Map map)
        {
            Thread.Sleep(300);
            return new[] { "legacy=ok" };
        }
    }

    public sealed class FixtureProvider : IBridgeAdapterProvider
    {
        private static int state;
        public BridgeAdapterMetadata Metadata => new BridgeAdapterMetadata
        {
            Id = "fixture",
            DisplayName = "Fixture",
            Version = "1.0.0",
            Generation = "fixture"
        };

        public IEnumerable<BridgeCommandDescriptor> Commands => new[]
        {
            Command("FIXTURE_ECHO", BridgeCommandMode.PureRead),
            Command("FIXTURE_READ", BridgeCommandMode.PureRead),
            Command("FIXTURE_SET", BridgeCommandMode.TemporaryTestMutation),
            Command("FIXTURE_RESET", BridgeCommandMode.TemporaryTestMutation),
            Command("FIXTURE_DELAY", BridgeCommandMode.PureRead),
            Command("FIXTURE_PARTIAL", BridgeCommandMode.PureRead)
        };

        public BridgeResult Execute(BridgeExecutionContext context)
        {
            switch (context.Request.Command)
            {
                case "FIXTURE_ECHO":
                    return BridgeResult.Ok("fixture.echo").Add("value", context.Request.Argument)
                        .Add("flag", true).Add("members", "alpha,beta,gamma");
                case "FIXTURE_READ":
                    return BridgeResult.Ok("fixture.state").Add("state", state);
                case "FIXTURE_SET":
                    if (!int.TryParse(context.Request.Argument, out int value))
                        return BridgeResult.Fail(BridgeStatus.INVALID_ARGUMENT, "fixture_integer_required");
                    int before = state;
                    state = value;
                    BridgeResult changed = BridgeResult.Ok("fixture.state").Add("before", before).Add("state", state);
                    changed.MutationSummary = "fixture state changed";
                    return changed;
                case "FIXTURE_RESET":
                    state = 0;
                    BridgeResult reset = BridgeResult.Ok("fixture.state").Add("state", state);
                    reset.MutationSummary = "fixture state reset";
                    return reset;
                case "FIXTURE_DELAY":
                    Thread.Sleep(Math.Max(0, Math.Min(250, int.TryParse(context.Request.Argument, out int delay)
                        ? delay : 0)));
                    return BridgeResult.Ok("fixture.delay").Add("delayed", true);
                case "FIXTURE_PARTIAL":
                    BridgeResult partial = BridgeResult.Ok("fixture.partial").Add("complete", false);
                    partial.Status = BridgeStatus.PARTIAL;
                    return partial;
                default:
                    return BridgeResult.Fail(BridgeStatus.NOT_FOUND, "fixture_command_not_found");
            }
        }

        private static BridgeCommandDescriptor Command(string name, BridgeCommandMode mode) =>
            new BridgeCommandDescriptor { Name = name, Mode = mode, Cost = BridgeCostClass.Trivial };
    }
}
