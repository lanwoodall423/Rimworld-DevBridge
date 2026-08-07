namespace RimWorldDevBridge.McpServer;

public sealed class McpServerOptions
{
    public string BridgeRoot { get; private init; } = "";
    public string UserRoot { get; private init; } = "";
    public string ClientPath { get; private init; } = "";
    public string PowerShellPath { get; private init; } = "powershell.exe";
    public int DefaultTimeoutMs { get; private init; } = 120000;

    public static McpServerOptions FromArgs(string[] args)
    {
        var bridgeRoot = GetOption(args, "--bridge-root") ??
            Environment.GetEnvironmentVariable("RIMWORLD_DEVBRIDGE_BRIDGE_ROOT") ?? "";
        var userRoot = GetOption(args, "--user-root") ??
            Environment.GetEnvironmentVariable("RIMWORLD_DEVBRIDGE_USER_ROOT") ?? "";
        var client = GetOption(args, "--client") ??
            Environment.GetEnvironmentVariable("RIMWORLD_DEVBRIDGE_CLIENT") ?? FindClientPath();
        var powerShell = GetOption(args, "--powershell") ??
            Environment.GetEnvironmentVariable("RIMWORLD_DEVBRIDGE_POWERSHELL") ?? "powershell.exe";
        var timeout = ParseBounded(GetOption(args, "--tool-timeout-ms"), 120000, 100, 600000);

        return new McpServerOptions
        {
            BridgeRoot = ValidateOptionalRoot(bridgeRoot, "bridge_root"),
            UserRoot = ValidateOptionalRoot(userRoot, "user_root"),
            ClientPath = ValidateOptionalFile(client, "client"),
            PowerShellPath = ValidateExecutable(powerShell),
            DefaultTimeoutMs = timeout
        };
    }

    private static string? GetOption(string[] args, string name)
    {
        for (var i = 0; i < args.Length; i++)
        {
            if (string.Equals(args[i], name, StringComparison.OrdinalIgnoreCase))
            {
                return i + 1 < args.Length ? args[i + 1] : null;
            }

            if (args[i].StartsWith(name + "=", StringComparison.OrdinalIgnoreCase))
            {
                return args[i][(name.Length + 1)..];
            }
        }

        return null;
    }

    private static string FindClientPath()
    {
        var candidates = new List<string>();
        var baseDirectory = new DirectoryInfo(AppContext.BaseDirectory);
        for (var current = baseDirectory; current != null; current = current.Parent)
        {
            candidates.Add(Path.Combine(current.FullName, "DevTools", "devbridge.ps1"));
            candidates.Add(Path.Combine(current.FullName, "devbridge.ps1"));
        }

        return candidates.FirstOrDefault(File.Exists) ?? "";
    }

    private static string ValidateOptionalRoot(string value, string name)
    {
        if (string.IsNullOrWhiteSpace(value))
        {
            return "";
        }

        var full = Path.GetFullPath(value);
        if (!Directory.Exists(full))
        {
            throw new InvalidOperationException($"{name}_invalid");
        }

        return full;
    }

    private static string ValidateOptionalFile(string value, string name)
    {
        if (string.IsNullOrWhiteSpace(value))
        {
            throw new InvalidOperationException($"{name}_missing");
        }

        var full = Path.GetFullPath(value);
        if (!File.Exists(full))
        {
            throw new InvalidOperationException($"{name}_invalid");
        }

        return full;
    }

    private static string ValidateExecutable(string value)
    {
        if (string.IsNullOrWhiteSpace(value))
        {
            throw new InvalidOperationException("powershell_missing");
        }

        return value;
    }

    private static int ParseBounded(string? value, int fallback, int minimum, int maximum)
    {
        if (string.IsNullOrWhiteSpace(value))
        {
            return fallback;
        }

        if (!int.TryParse(value, out var parsed) || parsed < minimum || parsed > maximum)
        {
            throw new InvalidOperationException("tool_timeout_invalid");
        }

        return parsed;
    }
}
