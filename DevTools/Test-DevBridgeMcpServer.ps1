param(
    [string]$ServerDll = (Join-Path $PSScriptRoot 'McpServer\bin\Release\net8.0\RimWorldDevBridge.McpServer.dll'),
    [string]$ClientPath = (Join-Path $PSScriptRoot 'devbridge.ps1')
)

$ErrorActionPreference = 'Stop'
$tempParent = Join-Path $env:TEMP 'RimWorldDevBridge-McpTests'
if (-not (Test-Path -LiteralPath $env:TEMP -PathType Container)) { throw 'temporary parent is missing' }
$testRoot = Join-Path $tempParent ([Guid]::NewGuid().ToString('N'))
$stderrTask = $null
$server = $null

function Assert-True([bool]$condition, [string]$message) {
    if (-not $condition) { throw $message }
}

function Send-Message([hashtable]$message) {
    $json = $message | ConvertTo-Json -Depth 10 -Compress
    $server.StandardInput.WriteLine($json)
    $server.StandardInput.Flush()
}

function Read-Message {
    $task = $server.StandardOutput.ReadLineAsync()
    if (-not $task.Wait(15000)) { throw 'MCP response timeout' }
    $line = $task.Result
    if ([string]::IsNullOrWhiteSpace($line)) { throw 'MCP server closed stdout' }
    try { return ($line | ConvertFrom-Json) } catch { throw "stdout was not JSON: $line" }
}

try {
    New-Item -ItemType Directory -Path $testRoot -Force | Out-Null
    Assert-True (Test-Path -LiteralPath $ServerDll -PathType Leaf) 'MCP server DLL is missing'
    Assert-True (Test-Path -LiteralPath $ClientPath -PathType Leaf) 'canonical client is missing'

    $startInfo = New-Object System.Diagnostics.ProcessStartInfo
    if ([IO.Path]::GetExtension($ServerDll) -ieq '.exe') {
        $startInfo.FileName = $ServerDll
        $startInfo.Arguments = ''
    } else {
        $startInfo.FileName = 'dotnet'
        $startInfo.Arguments = '"' + $ServerDll + '"'
    }
    $startInfo.UseShellExecute = $false
    $startInfo.CreateNoWindow = $true
    $startInfo.RedirectStandardInput = $true
    $startInfo.RedirectStandardOutput = $true
    $startInfo.RedirectStandardError = $true
    $startInfo.EnvironmentVariables['RIMWORLD_DEVBRIDGE_CLIENT'] = $ClientPath
    $startInfo.EnvironmentVariables['RIMWORLD_DEVBRIDGE_USER_ROOT'] = $testRoot
    $startInfo.EnvironmentVariables['RIMWORLD_DEVBRIDGE_BRIDGE_ROOT'] = (Split-Path -Parent (Split-Path -Parent $ClientPath))
    $server = New-Object System.Diagnostics.Process
    $server.StartInfo = $startInfo
    Assert-True $server.Start() 'MCP server did not start'
    $stderrTask = $server.StandardError.ReadToEndAsync()

    Send-Message @{ jsonrpc = '2.0'; id = 1; method = 'initialize'; params = @{ protocolVersion = '2025-06-18'; capabilities = @{}; clientInfo = @{ name = 'bridge-mcp-test'; version = '1' } } }
    $initialize = Read-Message
    $instructions = [string]$initialize.result.instructions
    foreach ($rule in @(
        'bridge_not_active is recoverable',
        'Complete autonomous work before waiting for human review',
        'Restart only coordinator-owned managed instances',
        'Every connected game is live and non-disposable by default',
        'Restart authorization is not mutation authorization',
        'Never claim or terminate a manual/external process')) {
        $position = $instructions.IndexOf($rule, [StringComparison]::Ordinal)
        Assert-True ($position -ge 0 -and $position -lt 512) "initialization instruction is outside the first 512 characters: $rule"
    }

    Send-Message @{ jsonrpc = '2.0'; method = 'notifications/initialized'; params = @{} }
    Send-Message @{ jsonrpc = '2.0'; id = 2; method = 'tools/list'; params = @{} }
    $tools = Read-Message
    $toolNames = @($tools.result.tools | ForEach-Object { $_.name })
    foreach ($name in @('ensure_bridge_ready', 'ensure_runtime_goal', 'get_runtime_goal_status', 'wait_for_goal', 'cancel_runtime_goal', 'checkpoint_runtime_goal', 'resume_runtime_goal', 'get_bridge_status', 'get_fresh_context', 'list_bridge_capabilities', 'run_read_only_query', 'validate_owner_adapter', 'request_managed_restart', 'wait_for_runtime', 'list_human_reviews', 'create_human_review', 'resolve_human_review', 'get_resume_checkpoint')) {
        Assert-True ($toolNames -contains $name) "missing MCP tool $name"
    }
    $ensureTool = @($tools.result.tools | Where-Object name -eq 'ensure_bridge_ready')[0]
    Assert-True ($ensureTool.annotations.readOnlyHint -eq $false -and $ensureTool.annotations.destructiveHint -eq $true) 'activation annotations are not truthful'
    $goalTool = @($tools.result.tools | Where-Object name -eq 'ensure_runtime_goal')[0]
    Assert-True ($goalTool.annotations.readOnlyHint -eq $false -and $goalTool.annotations.destructiveHint -eq $true) 'goal annotations are not truthful'
    $readTool = @($tools.result.tools | Where-Object name -eq 'run_read_only_query')[0]
    Assert-True ($readTool.annotations.readOnlyHint -eq $false) 'activation-capable read tool was incorrectly marked read-only'
    Assert-True ($null -ne $readTool.inputSchema -and $null -ne $readTool.outputSchema) 'tool schemas are incomplete'
    foreach ($tool in $tools.result.tools) {
        Assert-True ($null -ne $tool.inputSchema -and $null -ne $tool.outputSchema) "tool schema is incomplete: $($tool.name)"
        Assert-True ($null -ne $tool.annotations.readOnlyHint -and $null -ne $tool.annotations.destructiveHint -and $null -ne $tool.annotations.openWorldHint) "tool annotations are incomplete: $($tool.name)"
    }

    Send-Message @{ jsonrpc = '2.0'; id = 20; method = 'tools/call'; params = @{ name = 'missing_tool'; arguments = @{} } }
    $malformed = Read-Message
    Assert-True ($null -ne $malformed.error) 'unknown tool input did not produce a protocol error'

    Send-Message @{ jsonrpc = '2.0'; id = 3; method = 'tools/call'; params = @{ name = 'run_read_only_query'; arguments = @{ command = 'SET_SPEED'; correlationId = 'mcp-bypass-test' } } }
    $unsafe = Read-Message
    Assert-True ($unsafe.result.structuredContent.code -eq 'read_only_command_invalid') 'mutation command bypassed the read-only allowlist'
    Assert-True ($unsafe.result.structuredContent.operatorActionRequired -eq $false) 'read-only validation incorrectly requested an operator'

    Send-Message @{ jsonrpc = '2.0'; id = 4; method = 'tools/call'; params = @{ name = 'create_human_review'; arguments = @{ category = 'human_review'; taskId = 'mcp-review-task'; question = 'Choose a non-runtime documentation option.'; options = @('one', 'two'); recommendedDefault = 'one'; resumeOperation = 'review resume --request-id mcp-review'; correlationId = 'mcp-review-correlation'; artifactReferences = @('artifact://redacted') } } }
    $review = Read-Message
    Assert-True ($review.result.structuredContent.ok -eq $true) ("human review creation failed: " + ($review | ConvertTo-Json -Depth 10 -Compress))
    Assert-True ($review.result.structuredContent.data.request.authorization.authorizesMutation -eq $false) 'human review granted mutation authority'
    Assert-True ($review.result.structuredContent.data.request.authorization.grantsWriteLease -eq $false) ("human review granted a write lease: " + ($review | ConvertTo-Json -Depth 10 -Compress))

    Send-Message @{ jsonrpc = '2.0'; id = 5; method = 'tools/call'; params = @{ name = 'list_human_reviews'; arguments = @{ correlationId = 'mcp-list' } } }
    $list = Read-Message
    Assert-True ($list.result.structuredContent.ok -eq $true) 'human review list failed'
    $listText = $list.result.structuredContent.data | ConvertTo-Json -Depth 10 -Compress
    Assert-True (-not $listText.Contains($testRoot)) 'private user path leaked through MCP output'

    $requestId = [string]$review.result.structuredContent.data.request.requestId
    Assert-True (-not [string]::IsNullOrWhiteSpace($requestId)) 'review request ID was not returned'
    Send-Message @{ jsonrpc = '2.0'; id = 9; method = 'tools/call'; params = @{ name = 'resolve_human_review'; arguments = @{ requestId = $requestId; selectedOption = 'one'; answer = 'one'; correlationId = 'mcp-resolve' } } }
    $resolved = Read-Message
    Assert-True ($resolved.result.structuredContent.ok -eq $true) 'human review resolution failed'
    Send-Message @{ jsonrpc = '2.0'; id = 10; method = 'tools/call'; params = @{ name = 'get_resume_checkpoint'; arguments = @{ requestId = $requestId; correlationId = 'mcp-checkpoint' } } }
    $checkpoint = Read-Message
    Assert-True ($checkpoint.result.structuredContent.ok -eq $true) 'resume checkpoint retrieval failed'

    Send-Message @{ jsonrpc = '2.0'; id = 7; method = 'tools/call'; params = @{ name = 'ensure_bridge_ready'; arguments = @{ startupTimeoutMs = 1000; progressIntervalMs = 100; correlationId = 'mcp-activation' } } }
    $activation = Read-Message
    Assert-True ($activation.result.structuredContent.code -in @('managed_profile_missing', 'sandbox_authorization_missing', 'mcp_tool_timeout')) ("activation did not fail closed for an unconfigured managed profile: " + ($activation | ConvertTo-Json -Depth 10 -Compress))
    foreach ($field in @('recoverable', 'requiredAction', 'activationState', 'waitFor', 'keepRunning', 'retrySafe', 'operatorActionRequired', 'nextAction')) {
        Assert-True ($null -ne $activation.result.structuredContent.$field) "activation response field missing: $field"
    }

    Send-Message @{ jsonrpc = '2.0'; method = 'notifications/cancelled'; params = @{ requestId = 7; reason = 'test cancellation' } }
    Send-Message @{ jsonrpc = '2.0'; id = 8; method = 'tools/call'; params = @{ name = 'get_bridge_status'; arguments = @{ timeoutMs = 99; correlationId = 'mcp-invalid-timeout' } } }
    $cancelled = Read-Message
    Assert-True ($cancelled.result.structuredContent.code -eq 'timeoutMs_invalid') 'malformed/bounded timeout input was not rejected'

    Send-Message @{ jsonrpc = '2.0'; id = 6; method = 'tools/call'; params = @{ name = 'get_bridge_status'; arguments = @{ correlationId = 'mcp-status' } } }
    $status = Read-Message
    Assert-True ($null -ne $status.result.structuredContent.correlationId) 'correlation ID was not returned'

    $server.StandardInput.Close()
    if (-not $server.WaitForExit(15000)) { $server.Kill() }
    $serverStderr = if ($null -ne $stderrTask) { $stderrTask.Result } else { '' }
    Assert-True ($server.ExitCode -eq 0) "MCP server exit code was $($server.ExitCode)"
    'mcpProtocol=PASS initialization=PASS tools=PASS schemas=PASS annotations=PASS malformed=PASS redaction=PASS activation=PASS reviewSafety=PASS checkpoint=PASS cancellation=PASS'
}
catch {
    $diagnostic = if ($null -ne $stderrTask -and $stderrTask.IsCompleted) { $stderrTask.Result } else { '' }
    throw ("mcp_test_failed: " + $_.Exception.Message + "; server_stderr=" + $diagnostic)
}
finally {
    if ($null -ne $server) {
        try { if (-not $server.HasExited) { $server.Kill() } } catch { }
        $server.Dispose()
    }
    if (Test-Path -LiteralPath $testRoot) { Remove-Item -LiteralPath $testRoot -Recurse -Force }
    if (Test-Path -LiteralPath $tempParent) {
        $children = @(Get-ChildItem -LiteralPath $tempParent -Force -ErrorAction SilentlyContinue)
        if ($children.Count -eq 0) { Remove-Item -LiteralPath $tempParent -Force -ErrorAction SilentlyContinue }
    }
}
