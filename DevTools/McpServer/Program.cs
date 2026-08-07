using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using ModelContextProtocol.Server;

namespace RimWorldDevBridge.McpServer;

internal static class Program
{
    private const string Instructions =
        "bridge_not_active is recoverable for an authorized managed-test profile. " +
        "Complete autonomous work before waiting for human review. " +
        "Restart only coordinator-owned managed instances. " +
        "Every connected game is live and non-disposable by default. " +
        "Restart authorization is not mutation authorization. " +
        "Never claim or terminate a manual/external process. " +
        "Use read-only tools for reads; restart and review resolution remain separately authorized.";

    public static async Task Main(string[] args)
    {
        McpServerOptions options;
        try
        {
            options = McpServerOptions.FromArgs(args);
        }
        catch (Exception exception)
        {
            Console.Error.WriteLine("mcp_configuration_error: " + McpRedaction.RedactText(exception.Message));
            Environment.ExitCode = 3;
            return;
        }
        var builder = Host.CreateApplicationBuilder(args);
        builder.Logging.ClearProviders();
        builder.Logging.AddConsole(logging => logging.LogToStandardErrorThreshold = LogLevel.Trace);
        builder.Services.AddSingleton(options);
        builder.Services.AddSingleton<CanonicalClient>();
        builder.Services.AddSingleton<BridgeMcpTools>();
        builder.Services.AddMcpServer(server => server.ServerInstructions = Instructions)
            .WithStdioServerTransport()
            .WithTools<BridgeMcpTools>();

        await builder.Build().RunAsync().ConfigureAwait(false);
    }
}
