using System.ComponentModel;
using System.Text.Json;
using ModelContextProtocol.Server;

namespace RimWorldDevBridge.McpServer;

[McpServerToolType]
public sealed class BridgeMcpTools
{
    private const string PackageId = "Lan.RimWorldDevBridge";
    private readonly CanonicalClient _client;
    private readonly McpServerOptions _options;

    public BridgeMcpTools(CanonicalClient client, McpServerOptions options)
    {
        _client = client;
        _options = options;
    }

    [McpServerTool(ReadOnly = false, Destructive = true, OpenWorld = false, Idempotent = true,
        UseStructuredContent = true, OutputSchemaType = typeof(McpToolResponse))]
    [Description("Activate the authorized managed-test RimWorld instance, wait for bridge readiness, refresh context, and leave the coordinator-owned process running.")]
    public Task<McpToolResponse> EnsureBridgeReadyAsync(
        [Description("Bounded activation timeout in milliseconds.")] int startupTimeoutMs = 180000,
        [Description("Progress interval in milliseconds.")] int progressIntervalMs = 2000,
        [Description("Maximum managed launch attempts, from 1 through 5.")] int maxLaunchAttempts = 2,
        [Description("Backoff between managed launch attempts in milliseconds.")] int launchBackoffMs = 500,
        [Description("Caller correlation identifier.")] string? correlationId = null,
        CancellationToken cancellationToken = default)
    {
        var correlation = RequireCorrelation(correlationId, out var error);
        if (error != null) return Task.FromResult(error);
        if (!TryBounds(startupTimeoutMs, 100, 600000, "startupTimeoutMs", correlation!, out error) ||
            !TryBounds(progressIntervalMs, 100, 10000, "progressIntervalMs", correlation!, out error) ||
            !TryBounds(maxLaunchAttempts, 1, 5, "maxLaunchAttempts", correlation!, out error) ||
            !TryBounds(launchBackoffMs, 0, 10000, "launchBackoffMs", correlation!, out error))
        {
            return Task.FromResult(error!);
        }

        return _client.InvokeAsync("discover", new[]
        {
            $"--startup-timeout-ms={startupTimeoutMs}",
            $"--progress-interval-ms={progressIntervalMs}",
            $"--max-launch-attempts={maxLaunchAttempts}",
            $"--launch-backoff-ms={launchBackoffMs}"
        }, correlation!, startupTimeoutMs, cancellationToken);
    }

    [McpServerTool(ReadOnly = false, Destructive = false, OpenWorld = false, Idempotent = true,
        UseStructuredContent = true, OutputSchemaType = typeof(McpToolResponse))]
    [Description("Get current bridge status. This may perform the documented safe managed-test activation recovery when the bridge is inactive.")]
    public Task<McpToolResponse> GetBridgeStatusAsync(
        [Description("Bounded status and recovery timeout in milliseconds.")] int timeoutMs = 10000,
        [Description("Caller correlation identifier.")] string? correlationId = null,
        CancellationToken cancellationToken = default)
    {
        var correlation = Correlation(correlationId);
        if (!TryBounds(timeoutMs, 100, 600000, "timeoutMs", correlation, out var error)) return Task.FromResult(error!);
        return _client.InvokeAsync("discover", new[] { $"--startup-timeout-ms={timeoutMs}", "--progress-interval-ms=500" }, correlation, timeoutMs, cancellationToken);
    }

    [McpServerTool(ReadOnly = false, Destructive = false, OpenWorld = false, Idempotent = true,
        UseStructuredContent = true, OutputSchemaType = typeof(McpToolResponse))]
    [Description("Refresh the current package context after activation or a lifecycle transition.")]
    public Task<McpToolResponse> GetFreshContextAsync(
        [Description("Package ID used for the context handshake.")] string packageId = PackageId,
        [Description("Bounded context timeout in milliseconds.")] int timeoutMs = 120000,
        [Description("Caller correlation identifier.")] string? correlationId = null,
        CancellationToken cancellationToken = default)
    {
        if (!IsSafePackageId(packageId)) return Task.FromResult(McpToolResponse.Error("package_id_invalid", "The package ID is invalid.", Correlation(correlationId)));
        var correlation = Correlation(correlationId);
        if (!TryBounds(timeoutMs, 100, 600000, "timeoutMs", correlation, out var error)) return Task.FromResult(error!);
        return _client.InvokeAsync("context", new[] { $"--package-id={packageId}" }, correlation, timeoutMs, cancellationToken);
    }

    [McpServerTool(ReadOnly = false, Destructive = false, OpenWorld = false, Idempotent = true,
        UseStructuredContent = true, OutputSchemaType = typeof(McpToolResponse))]
    [Description("List the bridge capabilities and descriptors for the Dev Bridge package.")]
    public Task<McpToolResponse> ListBridgeCapabilitiesAsync(
        [Description("Package ID used for the context handshake.")] string packageId = PackageId,
        [Description("Caller correlation identifier.")] string? correlationId = null,
        CancellationToken cancellationToken = default)
    {
        if (!IsSafePackageId(packageId)) return Task.FromResult(McpToolResponse.Error("package_id_invalid", "The package ID is invalid.", Correlation(correlationId)));
        return InvokeSimple("describe", new[] { $"--package-id={packageId}" }, correlationId, _options.DefaultTimeoutMs, cancellationToken);
    }

    [McpServerTool(ReadOnly = false, Destructive = false, OpenWorld = false, Idempotent = true,
        UseStructuredContent = true, OutputSchemaType = typeof(McpToolResponse))]
    [Description("Run a whitelisted pure-read bridge query. Mutation, lease, reload, restart, and generic calls are rejected.")]
    public Task<McpToolResponse> RunReadOnlyQueryAsync(
        [Description("Known pure-read bridge command, such as STATUS, SESSION, AGENT_CONTEXT, PAWNS, THINGS, DEFS, JOBS, LOGS, or SCHEDULER_METRICS.")] string command,
        [Description("Bounded command argument.")] string argument = "",
        [Description("Bounded query timeout in milliseconds.")] int timeoutMs = 120000,
        [Description("Caller correlation identifier.")] string? correlationId = null,
        CancellationToken cancellationToken = default)
    {
        var correlation = Correlation(correlationId);
        if (string.IsNullOrWhiteSpace(command) || command.Length > 128)
            return Task.FromResult(McpToolResponse.Error("read_only_command_invalid", "The command is invalid.", correlation));
        if (!IsSafeText(argument, 4096, true))
            return Task.FromResult(McpToolResponse.Error("query_argument_invalid", "The query argument is invalid.", correlation));
        if (!TryBounds(timeoutMs, 100, 600000, "timeoutMs", correlation, out var error)) return Task.FromResult(error!);
        if (!ReadOnlyCommands.Contains(command.Trim().ToUpperInvariant()))
        {
            return Task.FromResult(McpToolResponse.Error("read_only_command_invalid", "The command is not in the explicit pure-read allowlist.", correlation));
        }

        return _client.InvokeAsync("read", new[] { $"--command={command.Trim().ToUpperInvariant()}", $"--argument={Bound(argument, 4096)}", $"--timeout-ms={timeoutMs}" }, correlation, timeoutMs, cancellationToken);
    }

    [McpServerTool(ReadOnly = false, Destructive = false, OpenWorld = true, Idempotent = true,
        UseStructuredContent = true, OutputSchemaType = typeof(McpToolResponse))]
    [Description("Validate an owner-controlled adapter repository using the repository's targeted owner validation script.")]
    public Task<McpToolResponse> ValidateOwnerAdapterAsync(
        [Description("Explicit owner repository path, when validating one owner.")] string? repositoryRoot = null,
        [Description("Owner package ID, when selecting a descriptor under the configured Mods root.")] string? packageId = null,
        [Description("Use strict broad audit mode.")] bool strict = false,
        [Description("Caller correlation identifier.")] string? correlationId = null,
        CancellationToken cancellationToken = default)
    {
        var correlation = Correlation(correlationId);
        if (string.IsNullOrWhiteSpace(repositoryRoot) && string.IsNullOrWhiteSpace(packageId))
        {
            return Task.FromResult(McpToolResponse.Error("owner_target_required", "Provide repositoryRoot or packageId.", correlation));
        }
        if (!string.IsNullOrWhiteSpace(repositoryRoot) && !IsSafeText(repositoryRoot, 2048))
        {
            return Task.FromResult(McpToolResponse.Error("repository_root_invalid", "The repository root is invalid.", correlation));
        }
        if (!string.IsNullOrWhiteSpace(packageId) && !IsSafePackageId(packageId))
        {
            return Task.FromResult(McpToolResponse.Error("package_id_invalid", "The package ID is invalid.", correlation));
        }

        var script = Path.Combine(Path.GetDirectoryName(_options.ClientPath) ?? "", "Test-OwnerBridgeAdapters.ps1");
        if (!File.Exists(script)) return Task.FromResult(McpToolResponse.Error("owner_validator_missing", "The owner validation script is not available.", correlation));
        var values = new List<string>();
        if (!string.IsNullOrWhiteSpace(repositoryRoot)) values.AddRange(new[] { "-RepositoryRoot", repositoryRoot! });
        if (!string.IsNullOrWhiteSpace(packageId)) values.AddRange(new[] { "-PackageId", packageId! });
        if (strict) values.Add("-Strict");
        return _client.InvokeScriptAsync(script, values, correlation, _options.DefaultTimeoutMs, cancellationToken);
    }

    [McpServerTool(ReadOnly = false, Destructive = true, OpenWorld = false,
        UseStructuredContent = true, OutputSchemaType = typeof(McpToolResponse))]
    [Description("Request bounded coordinator-owned managed-test restart/recovery with bridge readiness, no save copy, and keep-running.")]
    public Task<McpToolResponse> RequestManagedRestartAsync(
        [Description("Bounded restart timeout in milliseconds.")] int timeoutMs = 180000,
        [Description("Maximum managed launch attempts, from 1 through 5.")] int maxLaunchAttempts = 2,
        [Description("Backoff between managed launch attempts in milliseconds.")] int launchBackoffMs = 500,
        [Description("Caller correlation identifier.")] string? correlationId = null,
        CancellationToken cancellationToken = default)
    {
        var correlation = Correlation(correlationId);
        if (!TryBounds(timeoutMs, 100, 600000, "timeoutMs", correlation, out var error) ||
            !TryBounds(maxLaunchAttempts, 1, 5, "maxLaunchAttempts", correlation, out error) ||
            !TryBounds(launchBackoffMs, 0, 10000, "launchBackoffMs", correlation, out error)) return Task.FromResult(error!);
        return _client.InvokeAsync("restart", new[]
        {
            "ensure", "--readiness=bridge", "--save-policy=none", "--keep-running",
            $"--startup-timeout-ms={timeoutMs}", $"--timeout-ms={timeoutMs}",
            "--target-postcondition=bridge", "--requires-new-process", "--allow-supersede",
            $"--progress-interval-ms=2000",
            $"--max-launch-attempts={maxLaunchAttempts}", $"--launch-backoff-ms={launchBackoffMs}"
        }, correlation, timeoutMs, cancellationToken);
    }

    [McpServerTool(ReadOnly = true, Destructive = false, OpenWorld = false,
        UseStructuredContent = true, OutputSchemaType = typeof(McpToolResponse))]
    [Description("Wait for an existing coordinator ticket without claiming a process or granting mutation authority.")]
    public Task<McpToolResponse> WaitForRuntimeAsync(
        [Description("Existing coordinator restart ticket.")] string ticket,
        [Description("Bounded wait timeout in milliseconds.")] int timeoutMs = 120000,
        [Description("Caller correlation identifier.")] string? correlationId = null,
        CancellationToken cancellationToken = default)
    {
        var correlation = Correlation(correlationId);
        if (!IsSafeId(ticket)) return Task.FromResult(McpToolResponse.Error("ticket_invalid", "The ticket is invalid.", correlation));
        if (!TryBounds(timeoutMs, 100, 600000, "timeoutMs", correlation, out var error)) return Task.FromResult(error!);
        return _client.InvokeAsync("restart", new[] { "wait", $"--ticket={ticket}", $"--timeout-ms={timeoutMs}" }, correlation, timeoutMs, cancellationToken);
    }

    [McpServerTool(ReadOnly = true, Destructive = false, OpenWorld = false, Idempotent = true,
        UseStructuredContent = true, OutputSchemaType = typeof(McpToolResponse))]
    [Description("List durable human review tickets. Review never authorizes gameplay, attached-process control, or a write lease.")]
    public Task<McpToolResponse> ListHumanReviewsAsync(
        [Description("Caller correlation identifier.")] string? correlationId = null,
        CancellationToken cancellationToken = default)
    {
        return InvokeSimple("review", new[] { "list" }, correlationId, _options.DefaultTimeoutMs, cancellationToken);
    }

    [McpServerTool(ReadOnly = false, Destructive = false, OpenWorld = false,
        UseStructuredContent = true, OutputSchemaType = typeof(McpToolResponse))]
    [Description("Create a durable human review ticket. This never grants mutation, attached-process control, or a write lease.")]
    public Task<McpToolResponse> CreateHumanReviewAsync(
        [Description("human_review, human_approval, or hard_blocker.")] string category,
        [Description("Stable task identifier.")] string taskId,
        [Description("Exact question requiring human judgment.")] string question,
        [Description("Concrete options, up to three; at least two for human_review/human_approval.")] string[] options,
        [Description("Recommended default option.")] string recommendedDefault,
        [Description("Exact operation to run when resumed.")] string resumeOperation,
        [Description("Completed autonomous work.")] string completedWork = "",
        [Description("Verification evidence.")] string verificationEvidence = "",
        [Description("Dependent work that remains.")] string dependentWork = "",
        [Description("Independent work that will continue.")] string independentWork = "",
        [Description("Whether runtime state must be preserved.")] bool preserveRuntime = false,
        [Description("Screenshot or artifact references.")] string[]? artifactReferences = null,
        [Description("Screenshot references.")] string[]? screenshotReferences = null,
        [Description("Response window in milliseconds.")] int responseTimeoutMs = 60000,
        [Description("Deduplication key.")] string? deduplicationKey = null,
        [Description("Caller correlation identifier.")] string? correlationId = null,
        CancellationToken cancellationToken = default)
    {
        var correlation = Correlation(correlationId);
        if (!IsSafeId(taskId) || !IsSafeText(question, 4096) || !IsSafeText(resumeOperation, 512) ||
            !IsSafeText(recommendedDefault, 512) || options is null || options.Length > 3 ||
            options.Any(option => !IsSafeText(option, 4096)) ||
            !IsSafeText(completedWork, 4096, true) || !IsSafeText(verificationEvidence, 4096, true) ||
            !IsSafeText(dependentWork, 4096, true) || !IsSafeText(independentWork, 4096, true) ||
            !IsSafeReferences(artifactReferences) || !IsSafeReferences(screenshotReferences) ||
            (!string.IsNullOrWhiteSpace(deduplicationKey) && !IsSafeId(deduplicationKey)))
            return Task.FromResult(McpToolResponse.Error("review_input_invalid", "Review task, question, options, and resume operation are invalid.", correlation));
        if (category is not ("human_review" or "human_approval" or "hard_blocker"))
            return Task.FromResult(McpToolResponse.Error("review_category_invalid", "The review category is invalid.", correlation));
        if (category != "hard_blocker" && options.Length < 2)
            return Task.FromResult(McpToolResponse.Error("review_options_required", "At least two review options are required.", correlation));
        if (!TryBounds(responseTimeoutMs, 1000, 600000, "responseTimeoutMs", correlation, out var error)) return Task.FromResult(error!);
        var values = new List<string> { "request", $"--category={category}", $"--task-id={taskId}", $"--question={question}", $"--recommended={recommendedDefault}", $"--resume-operation={resumeOperation}", $"--completed-work={completedWork}", $"--verification-evidence={verificationEvidence}", $"--dependent-work={dependentWork}", $"--independent-work={independentWork}", $"--response-timeout-ms={responseTimeoutMs}", "--preserve-runtime=" + preserveRuntime };
        AddOptions(values, "--option-", options);
        AddOptions(values, "--artifact-ref", artifactReferences);
        AddOptions(values, "--screenshot-ref", screenshotReferences);
        if (!string.IsNullOrWhiteSpace(deduplicationKey)) values.Add($"--dedup-key={deduplicationKey}");
        return _client.InvokeAsync("review", values, correlation, _options.DefaultTimeoutMs, cancellationToken);
    }

    [McpServerTool(ReadOnly = false, Destructive = false, OpenWorld = false,
        UseStructuredContent = true, OutputSchemaType = typeof(McpToolResponse))]
    [Description("Resolve a human review ticket. Resolution remains separate from gameplay mutation and safety confirmation.")]
    public Task<McpToolResponse> ResolveHumanReviewAsync(
        [Description("Review request ID.")] string requestId,
        [Description("Selected option, if applicable.")] string selectedOption = "",
        [Description("Human answer.")] string answer = "",
        [Description("Resolution category.")] string resolution = "human_response",
        [Description("Caller correlation identifier.")] string? correlationId = null,
        CancellationToken cancellationToken = default)
    {
        var correlation = Correlation(correlationId);
        if (!IsSafeId(requestId)) return Task.FromResult(McpToolResponse.Error("review_request_id_invalid", "The review request ID is invalid.", correlation));
        if (!IsSafeText(selectedOption, 512, true) || !IsSafeText(answer, 4096, true) || !IsSafeText(resolution, 128))
            return Task.FromResult(McpToolResponse.Error("review_resolution_invalid", "The review resolution is invalid.", correlation));
        return _client.InvokeAsync("review", new[] { "resolve", $"--request-id={requestId}", $"--selected-option={selectedOption}", $"--answer={answer}", $"--resolution={resolution}" }, correlation, _options.DefaultTimeoutMs, cancellationToken);
    }

    [McpServerTool(ReadOnly = true, Destructive = false, OpenWorld = false, Idempotent = true,
        UseStructuredContent = true, OutputSchemaType = typeof(McpToolResponse))]
    [Description("Get a durable review request and its resume checkpoint. Review authorization fields are always false.")]
    public Task<McpToolResponse> GetResumeCheckpointAsync(
        [Description("Review request ID.")] string requestId,
        [Description("Caller correlation identifier.")] string? correlationId = null,
        CancellationToken cancellationToken = default)
    {
        var correlation = Correlation(correlationId);
        if (!IsSafeId(requestId)) return Task.FromResult(McpToolResponse.Error("review_request_id_invalid", "The review request ID is invalid.", correlation));
        return _client.InvokeAsync("review", new[] { "get", $"--request-id={requestId}" }, correlation, _options.DefaultTimeoutMs, cancellationToken);
    }

    private Task<McpToolResponse> InvokeSimple(string operation, IEnumerable<string> values, string? correlationId,
        int timeoutMs, CancellationToken cancellationToken)
    {
        var correlation = Correlation(correlationId);
        return _client.InvokeAsync(operation, values, correlation, timeoutMs, cancellationToken);
    }

    private static readonly HashSet<string> ReadOnlyCommands = new(StringComparer.OrdinalIgnoreCase)
    {
        "STATUS", "SESSION", "AGENT_CONTEXT", "SCHEDULER_METRICS", "HEALTH", "PAWNS", "THINGS", "DEFS", "JOBS", "LOGS", "EVENTS", "HARMONY", "PERFORMANCE"
    };

    private static void AddOptions(List<string> values, string prefix, IReadOnlyList<string>? options)
    {
        if (options is null) return;
        for (var i = 0; i < options.Count; i++)
        {
            if (!string.IsNullOrWhiteSpace(options[i])) values.Add(prefix == "--option-" ? $"{prefix}{i + 1}={Bound(options[i], 4096)}" : $"{prefix}={Bound(options[i], 4096)}");
        }
    }

    private static string Bound(string? value, int length) => string.IsNullOrWhiteSpace(value) ? "" : value.Length <= length ? value : value[..length];

    private static bool IsSafePackageId(string? value) => !string.IsNullOrWhiteSpace(value) && value.Length <= 128 && value.All(c => char.IsLetterOrDigit(c) || c is '.' or '-' or '_');
    private static bool IsSafeId(string? value) => !string.IsNullOrWhiteSpace(value) && value.Length <= 128 && value.All(c => char.IsLetterOrDigit(c) || c is '.' or '-' or '_' or ':');
    private static bool IsSafeText(string? value, int maximum) => !string.IsNullOrWhiteSpace(value) && value.Length <= maximum && value.All(c => !char.IsControl(c));
    private static bool IsSafeText(string? value, int maximum, bool allowEmpty) => (allowEmpty || !string.IsNullOrWhiteSpace(value)) && value is not null && value.Length <= maximum && value.All(c => !char.IsControl(c));
    private static bool IsSafeReferences(IReadOnlyList<string>? references) => references is null || references.Count <= 8 && references.All(reference => IsSafeText(reference, 512));

    private static string Correlation(string? value) => string.IsNullOrWhiteSpace(value) ? Guid.NewGuid().ToString("N") : value;

    private static string? RequireCorrelation(string? value, out McpToolResponse? error)
    {
        var correlation = Correlation(value);
        if (!IsSafeId(correlation))
        {
            error = McpToolResponse.Error("correlation_id_invalid", "The correlation ID is invalid.", correlation);
            return null;
        }

        error = null;
        return correlation;
    }

    private static bool TryBounds(int value, int minimum, int maximum, string name, string correlation, out McpToolResponse? error)
    {
        if (value < minimum || value > maximum)
        {
            error = McpToolResponse.Error(name + "_invalid", $"{name} must be between {minimum} and {maximum}.", correlation);
            return false;
        }

        error = null;
        return true;
    }
}
