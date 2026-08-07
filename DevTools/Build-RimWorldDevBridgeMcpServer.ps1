[CmdletBinding()]
param(
    [string]$OutputDirectory = (Join-Path (Split-Path -Parent $PSScriptRoot) 'McpServerArtifact'),
    [ValidateSet('win-x64', 'win-arm64')][string]$RuntimeIdentifier = 'win-x64',
    [bool]$SelfContained = $true
)

$ErrorActionPreference = 'Stop'
$root = [IO.Path]::GetFullPath((Split-Path -Parent $PSScriptRoot))
$project = Join-Path $PSScriptRoot 'McpServer/RimWorldDevBridge.McpServer.csproj'
$output = [IO.Path]::GetFullPath($OutputDirectory)

if (-not (Test-Path -LiteralPath $project -PathType Leaf)) { throw 'MCP project is missing.' }
if ((Test-Path -LiteralPath $output -PathType Container) -and (@(Get-ChildItem -LiteralPath $output -Force).Count -gt 0)) {
    throw "Refusing to replace a non-empty MCP output directory: $output"
}
New-Item -ItemType Directory -Path $output -Force | Out-Null

$selfContainedValue = $SelfContained.ToString().ToLowerInvariant()
& dotnet publish $project -c Release -r $RuntimeIdentifier --self-contained:$selfContainedValue `
    -o $output -p:PublishSingleFile=true -p:IncludeNativeLibrariesForSelfExtract=true `
    -p:DebugType=None -p:DebugSymbols=false -p:PublishTrimmed=false
if ($LASTEXITCODE -ne 0) { throw "MCP publish failed with exit code $LASTEXITCODE." }

$executable = Join-Path $output 'RimWorldDevBridge.McpServer.exe'
if (-not (Test-Path -LiteralPath $executable -PathType Leaf)) { throw "MCP executable was not published: $executable" }
Copy-Item -LiteralPath (Join-Path $PSScriptRoot 'McpServer/THIRD_PARTY_NOTICES.md') -Destination $output

$forbidden = @(Get-ChildItem -LiteralPath $output -Recurse -Force | Where-Object {
    $_.FullName -match '(?i)(node_modules|(^|[\\/])(bin|obj|tests?|test-output|build-output|\.git)([\\/]|$)|\.pdb$|\.nupkg$|\.zip$)'
})
if ($forbidden.Count -gt 0) { throw "MCP artifact contains prohibited output: $($forbidden[0].FullName)" }

$hash = (Get-FileHash -LiteralPath $executable -Algorithm SHA256).Hash.ToUpperInvariant()
Write-Output "mcpPublish=PASS runtime=$RuntimeIdentifier selfContained=$SelfContained executable=$([IO.Path]::GetFileName($executable)) sha256=$hash"
