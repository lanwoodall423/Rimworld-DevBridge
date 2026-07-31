function Remove-OwnedBridgeArtifacts {
    param(
        [int]$ProcessId,
        [string]$BootId,
        [string]$StatusPath,
        [string[]]$ArtifactPaths
    )

    if (-not (Test-Path -LiteralPath $StatusPath -PathType Leaf)) { return $false }
    $status = @{}
    try {
        foreach ($line in [IO.File]::ReadAllLines($StatusPath)) {
            $split = $line.IndexOf("=")
            if ($split -gt 0) { $status[$line.Substring(0, $split)] = $line.Substring($split + 1) }
        }
    }
    catch [IO.IOException] { return $false }

    if ($status["processId"] -ne "$ProcessId") { return $false }
    if (-not [string]::IsNullOrWhiteSpace($BootId) -and $status["bootId"] -ne $BootId) { return $false }
    foreach ($path in $ArtifactPaths) {
        if (Test-Path -LiteralPath $path -PathType Leaf) { [IO.File]::Delete($path) }
    }
    return $true
}
