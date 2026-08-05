param(
    [Parameter(Position = 0)][string]$Command = "SYNC",
    [Parameter(Position = 1)][string]$Argument = "",
    [int]$TimeoutMs = 5000,
    [string]$Options = "",
    [switch]$NoExitOnRestartRequired,
    [string]$BridgeRoot,
    [string]$UserRoot,
    [string]$AgentId,
    [string]$WorkspaceId
)

$ErrorActionPreference = "Stop"
Write-Warning "Send-RimWorldBridge.ps1 is deprecated; use DevTools/devbridge.ps1 call instead."
$client = Join-Path $PSScriptRoot "devbridge.ps1"
if (-not (Test-Path -LiteralPath $client -PathType Leaf)) { throw "canonical devbridge.ps1 is missing" }

$arguments = @("call", $Command, ("--argument=" + $Argument), ("--timeout-ms=" + $TimeoutMs))
if (-not [string]::IsNullOrWhiteSpace($BridgeRoot)) { $arguments += ("--bridge-root=" + $BridgeRoot) }
if (-not [string]::IsNullOrWhiteSpace($UserRoot)) { $arguments += ("--user-root=" + $UserRoot) }
if (-not [string]::IsNullOrWhiteSpace($AgentId)) { $arguments += ("--agent-id=" + $AgentId) }
if (-not [string]::IsNullOrWhiteSpace($WorkspaceId)) { $arguments += ("--workspace-id=" + $WorkspaceId) }
foreach ($option in ($Options -split '&')) {
    if (-not [string]::IsNullOrWhiteSpace($option)) { $arguments += ("--option=" + $option) }
}
if ($NoExitOnRestartRequired) { $arguments += "--no-exit-on-restart-required" }

& powershell.exe -NoProfile -ExecutionPolicy Bypass -File $client @arguments
exit $LASTEXITCODE
