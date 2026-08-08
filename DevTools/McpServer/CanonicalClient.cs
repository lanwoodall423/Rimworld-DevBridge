using System.Diagnostics;
using System.Text.Json;

namespace RimWorldDevBridge.McpServer;

public sealed class CanonicalClient
{
    private readonly McpServerOptions _options;

    public CanonicalClient(McpServerOptions options)
    {
        _options = options;
    }

    public Task<McpToolResponse> InvokeAsync(string operation, IEnumerable<string> values,
        string correlationId, int timeoutMs, CancellationToken cancellationToken)
    {
        var arguments = new List<string> { operation };
        arguments.AddRange(values);
        return RunAsync(arguments, correlationId, timeoutMs, cancellationToken, true, true);
    }

    public Task<McpToolResponse> InvokeScriptAsync(string scriptPath, IEnumerable<string> values,
        string correlationId, int timeoutMs, CancellationToken cancellationToken)
    {
        var arguments = new List<string> { "-NoProfile", "-ExecutionPolicy", "Bypass", "-File", scriptPath };
        arguments.AddRange(values);
        return RunAsync(arguments, correlationId, timeoutMs, cancellationToken, false, false);
    }

    private async Task<McpToolResponse> RunAsync(IReadOnlyList<string> arguments, string correlationId,
        int timeoutMs, CancellationToken cancellationToken, bool includeClientRoots, bool parseJson)
    {
        if (includeClientRoots && string.IsNullOrWhiteSpace(_options.ClientPath))
        {
            return McpToolResponse.Error("client_missing", "The canonical client is not configured.", correlationId);
        }

        timeoutMs = Math.Clamp(timeoutMs, 100, 600000);
        using var process = new Process
        {
            StartInfo = new ProcessStartInfo
            {
                FileName = _options.PowerShellPath,
                UseShellExecute = false,
                CreateNoWindow = true,
                RedirectStandardInput = true,
                RedirectStandardOutput = true,
                RedirectStandardError = true
            }
        };
        foreach (var argument in arguments)
        {
            process.StartInfo.ArgumentList.Add(argument);
        }
        if (arguments.Count > 0 && arguments[0] != "-NoProfile")
        {
            process.StartInfo.ArgumentList.Insert(0, "-NoProfile");
            process.StartInfo.ArgumentList.Insert(1, "-ExecutionPolicy");
            process.StartInfo.ArgumentList.Insert(2, "Bypass");
            process.StartInfo.ArgumentList.Insert(3, "-File");
            process.StartInfo.ArgumentList.Insert(4, _options.ClientPath);
        }

            if (includeClientRoots)
            {
                AddClientRoots(process.StartInfo, correlationId);
        }
        try
        {
            if (!process.Start())
            {
                return McpToolResponse.Error("client_start_failed", "The canonical client could not start.", correlationId, true,
                    "check the configured canonical client", retrySafe: true);
            }
            process.StandardInput.Close();

            var stdoutTask = process.StandardOutput.ReadToEndAsync(cancellationToken);
            var stderrTask = process.StandardError.ReadToEndAsync(cancellationToken);
            var exitTask = process.WaitForExitAsync(cancellationToken);
            var completed = await Task.WhenAny(exitTask, Task.Delay(timeoutMs)).ConfigureAwait(false);
            if (completed != exitTask)
            {
                TryKill(process);
                return McpToolResponse.Error("mcp_tool_timeout", "The canonical client exceeded its bounded timeout.", correlationId,
                    true, "retry the bounded operation", "bridge", true);
            }

            await exitTask.ConfigureAwait(false);
            var stdout = await stdoutTask.ConfigureAwait(false);
            var stderr = await stderrTask.ConfigureAwait(false);
            if (!parseJson)
            {
                var output = McpRedaction.RedactText((stdout + Environment.NewLine + stderr).Trim());
                var data = JsonSerializer.SerializeToElement(new { exitCode = process.ExitCode, output });
                return new McpToolResponse
                {
                    Ok = process.ExitCode == 0,
                    Code = process.ExitCode == 0 ? "owner_validation_passed" : "owner_validation_failed",
                    Message = process.ExitCode == 0 ? "Owner validation passed." : "Owner validation failed.",
                    CorrelationId = correlationId,
                    ActivationState = "ready",
                    WaitFor = "none",
                    Recoverable = false,
                    RequiredAction = process.ExitCode == 0 ? "none" : "inspect the sanitized validation output",
                    KeepRunning = true,
                    RetrySafe = true,
                    OperatorActionRequired = false,
                    NextAction = process.ExitCode == 0 ? "none" : "fix the owner adapter and retry",
                    Data = data
                };
            }
            var json = FindJson(stdout);
            if (json is null)
            {
                var detail = string.IsNullOrWhiteSpace(stderr) ? "The canonical client returned no JSON." : stderr;
                return McpToolResponse.Error("mcp_protocol_error", detail, correlationId);
            }

            var response = McpToolResponse.FromJson(json.Value, correlationId);
            return response with
            {
                AgentId = string.IsNullOrWhiteSpace(response.AgentId) ? _options.AgentId : response.AgentId,
                ClientInstanceId = string.IsNullOrWhiteSpace(response.ClientInstanceId) ? _options.ClientInstanceId : response.ClientInstanceId
            };
        }
        catch (OperationCanceledException)
        {
            TryKill(process);
            return McpToolResponse.Error("mcp_cancelled", "The MCP operation was cancelled.", correlationId, true,
                "retry the operation when ready", retrySafe: true);
        }
        catch (Exception exception)
        {
            TryKill(process);
            return McpToolResponse.Error("mcp_client_error", exception.Message, correlationId);
        }
    }

    private void AddClientRoots(ProcessStartInfo startInfo, string correlationId)
    {
        if (!string.IsNullOrWhiteSpace(_options.BridgeRoot))
        {
            startInfo.ArgumentList.Add("--bridge-root");
            startInfo.ArgumentList.Add(_options.BridgeRoot);
        }

        if (!string.IsNullOrWhiteSpace(_options.UserRoot))
        {
            startInfo.ArgumentList.Add("--user-root");
            startInfo.ArgumentList.Add(_options.UserRoot);
        }

        startInfo.ArgumentList.Add("--agent-id");
        startInfo.ArgumentList.Add(_options.AgentId);
        startInfo.ArgumentList.Add("--client-instance-id");
        startInfo.ArgumentList.Add(_options.ClientInstanceId);
        startInfo.ArgumentList.Add("--connection-session-id");
        startInfo.ArgumentList.Add(string.IsNullOrWhiteSpace(_options.ConnectionSessionId)
            ? "connection-" + Guid.NewGuid().ToString("N") : _options.ConnectionSessionId);
        startInfo.ArgumentList.Add("--correlation-id");
        startInfo.ArgumentList.Add(correlationId);

        startInfo.ArgumentList.Add("--json");
    }

    private static JsonElement? FindJson(string stdout)
    {
        var lines = stdout.Split(new[] { '\r', '\n' }, StringSplitOptions.RemoveEmptyEntries);
        for (var index = lines.Length - 1; index >= 0; index--)
        {
            try
            {
                using var document = JsonDocument.Parse(lines[index].Trim());
                return document.RootElement.Clone();
            }
            catch (JsonException)
            {
                // Continue until the final JSON document is found.
            }
        }

        return null;
    }

    private static void TryKill(Process process)
    {
        try
        {
            if (!process.HasExited)
            {
                process.Kill(entireProcessTree: true);
            }
        }
        catch
        {
            // The client owns cleanup of its child process; never expose cleanup details over MCP.
        }
    }
}
