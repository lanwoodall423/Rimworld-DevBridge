using System.Text.Json;
using System.Text.Json.Nodes;
using System.Text.RegularExpressions;

namespace RimWorldDevBridge.McpServer;

public sealed record McpToolResponse
{
    public bool Ok { get; init; }
    public string Code { get; init; } = "mcp_error";
    public string Message { get; init; } = "";
    public string CorrelationId { get; init; } = "";
    public string ActivationState { get; init; } = "inactive";
    public string WaitFor { get; init; } = "none";
    public bool Recoverable { get; init; }
    public string RequiredAction { get; init; } = "none";
    public bool KeepRunning { get; init; }
    public bool RetrySafe { get; init; }
    public bool OperatorActionRequired { get; init; }
    public string NextAction { get; init; } = "none";
    public JsonElement? Data { get; init; }

    public static McpToolResponse FromJson(JsonElement root, string correlationId)
    {
        var ok = ReadBool(root, "ok") || ReadBool(root, "available") ||
            string.Equals(ReadString(root, "status"), "available", StringComparison.OrdinalIgnoreCase) ||
            string.Equals(ReadString(root, "status"), "OK", StringComparison.OrdinalIgnoreCase);
        var code = FirstNonEmpty(ReadString(root, "reason"), ReadString(root, "error"), ok ? "ok" : "bridge_unavailable");
        var activation = FirstNonEmpty(ReadString(root, "activationState"), ReadString(root, "state"), ok ? "ready" : "inactive");
        var waitFor = FirstNonEmpty(ReadString(root, "waitFor"), ok ? "none" : "bridge");
        var message = McpRedaction.RedactText(FirstNonEmpty(ReadString(root, "detail"), ReadString(root, "message"), code));
        var redactedRoot = McpRedaction.RedactJson(root);

        return new McpToolResponse
        {
            Ok = ok,
            Code = code,
            Message = message,
            CorrelationId = correlationId,
            ActivationState = activation,
            WaitFor = waitFor,
            Recoverable = ReadBool(root, "recoverable", !ok && IsRecoverableCode(code)),
            RequiredAction = McpRedaction.RedactText(FirstNonEmpty(ReadString(root, "requiredAction"), ok ? "none" : "activate authorized managed-test instance")),
            KeepRunning = ReadBool(root, "keepRunning", ok),
            RetrySafe = ReadBool(root, "retrySafe", ok && IsReadResult(root)),
            OperatorActionRequired = ReadBool(root, "operatorActionRequired", code == "attached_live_process_requires_operator"),
            NextAction = McpRedaction.RedactText(FirstNonEmpty(ReadString(root, "nextAction"), ok ? "none" : "retry bounded recovery")),
            Data = redactedRoot
        };
    }

    public static McpToolResponse Error(string code, string message, string correlationId,
        bool recoverable = false, string requiredAction = "none", string waitFor = "none",
        bool retrySafe = false, bool operatorActionRequired = false)
    {
        return new McpToolResponse
        {
            Ok = false,
            Code = code,
            Message = McpRedaction.RedactText(message),
            CorrelationId = correlationId,
            ActivationState = code == "activation_in_progress" ? "activation_in_progress" : "failed",
            WaitFor = waitFor,
            Recoverable = recoverable,
            RequiredAction = requiredAction,
            KeepRunning = true,
            RetrySafe = retrySafe,
            OperatorActionRequired = operatorActionRequired,
            NextAction = recoverable ? "retry bounded managed recovery" : "none"
        };
    }

    private static bool IsReadResult(JsonElement root) =>
        string.Equals(ReadString(root, "operation"), "read", StringComparison.OrdinalIgnoreCase) ||
        string.Equals(ReadString(root, "command"), "STATUS", StringComparison.OrdinalIgnoreCase);

    private static bool IsRecoverableCode(string code) => code is
        "bridge_not_active" or "status_unavailable" or "process_not_running" or "stale_status" or
        "disk_runtime_mismatch" or "core_fingerprint_mismatch" or "bridge_did_not_wake" or
        "activation_in_progress" or "managed_process_exited_before_ready" or "managed_launch_retrying" or
        "managed_launch_failed" or "bridge_handshake_timeout" or "bridge_load_failed";

    private static string FirstNonEmpty(params string?[] values) =>
        values.FirstOrDefault(value => !string.IsNullOrWhiteSpace(value)) ?? "";

    private static string? ReadString(JsonElement root, string name)
    {
        return root.ValueKind == JsonValueKind.Object && root.TryGetProperty(name, out var value) &&
            value.ValueKind == JsonValueKind.String ? value.GetString() : null;
    }

    private static bool ReadBool(JsonElement root, string name, bool fallback = false)
    {
        return root.ValueKind == JsonValueKind.Object && root.TryGetProperty(name, out var value) &&
            (value.ValueKind == JsonValueKind.True || value.ValueKind == JsonValueKind.False)
            ? value.GetBoolean() : fallback;
    }
}

internal static class McpRedaction
{
    private static readonly Regex Secret = new(
        @"(?i)(token|lease|authToken|secret|password)\s*[:=]\s*[^,;|\s]+",
        RegexOptions.Compiled | RegexOptions.CultureInvariant);
    private static readonly Regex WindowsPath = new(
        @"(?i)([a-z]:\\|\\\\|/users/|/home/|appdata|locallow)[^\r\n,;|]*",
        RegexOptions.Compiled | RegexOptions.CultureInvariant);

    public static string RedactText(string value)
    {
        if (string.IsNullOrWhiteSpace(value))
        {
            return "";
        }

        var redacted = Secret.Replace(value, "$1=[redacted]");
        return WindowsPath.Replace(redacted, "[private-path]");
    }

    public static JsonElement RedactJson(JsonElement value)
    {
        var node = JsonNode.Parse(value.GetRawText());
        RedactNode(node, null);
        return JsonSerializer.SerializeToElement(node, new JsonSerializerOptions { PropertyNamingPolicy = JsonNamingPolicy.CamelCase });
    }

    private static void RedactNode(JsonNode? node, string? propertyName)
    {
        if (node is JsonObject objectNode)
        {
            foreach (var property in objectNode.ToList())
            {
                if (IsSensitive(property.Key))
                {
                    objectNode[property.Key] = "[redacted]";
                }
                else
                {
                    RedactNode(property.Value, property.Key);
                }
            }
        }
        else if (node is JsonArray arrayNode)
        {
            for (var index = 0; index < arrayNode.Count; index++)
            {
                if (arrayNode[index] is JsonValue arrayValue && arrayValue.TryGetValue<string>(out var arrayText))
                {
                    arrayNode[index] = RedactText(arrayText);
                }
                else
                {
                    RedactNode(arrayNode[index], propertyName);
                }
            }
        }
        else if (node is JsonValue valueNode && valueNode.TryGetValue<string>(out var text))
        {
            valueNode.ReplaceWith(RedactText(text));
        }
    }

    private static bool IsSensitive(string name) =>
        name.Equals("token", StringComparison.OrdinalIgnoreCase) ||
        name.EndsWith("Token", StringComparison.OrdinalIgnoreCase) ||
        name.Equals("lease", StringComparison.OrdinalIgnoreCase) ||
        name.Equals("secret", StringComparison.OrdinalIgnoreCase) ||
        name.Equals("password", StringComparison.OrdinalIgnoreCase) ||
        name.Equals("path", StringComparison.OrdinalIgnoreCase) ||
        name.EndsWith("Path", StringComparison.OrdinalIgnoreCase) ||
        name.Equals("bridgeRoot", StringComparison.OrdinalIgnoreCase) ||
        name.Equals("statusPath", StringComparison.OrdinalIgnoreCase) ||
        name.Equals("gamePath", StringComparison.OrdinalIgnoreCase) ||
        name.Equals("savePath", StringComparison.OrdinalIgnoreCase) ||
        name.Equals("profilePath", StringComparison.OrdinalIgnoreCase) ||
        name.Equals("root", StringComparison.OrdinalIgnoreCase) ||
        name.Equals("userRoot", StringComparison.OrdinalIgnoreCase) ||
        name.Equals("executable", StringComparison.OrdinalIgnoreCase) ||
        name.Equals("workingDirectory", StringComparison.OrdinalIgnoreCase) ||
        name.Equals("userDataRoot", StringComparison.OrdinalIgnoreCase) ||
        name.Equals("gameIdentity", StringComparison.OrdinalIgnoreCase) ||
        name.Equals("saveIdentity", StringComparison.OrdinalIgnoreCase) ||
        name.Equals("saveName", StringComparison.OrdinalIgnoreCase) ||
        name.Equals("saveFile", StringComparison.OrdinalIgnoreCase) ||
        name.Equals("username", StringComparison.OrdinalIgnoreCase) ||
        name.Equals("userName", StringComparison.OrdinalIgnoreCase);
}
