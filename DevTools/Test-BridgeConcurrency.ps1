param(
    [ValidateRange(2,16)][int]$Clients = 8,
    [int]$TimeoutMs = 10000
)

$clientPath = Join-Path $PSScriptRoot "devbridge.ps1"
$pool = [RunspaceFactory]::CreateRunspacePool(1, $Clients)
$pool.Open()
$calls = @()

try {
    $escapedPath = $clientPath.Replace("'", "''")
    foreach ($number in 1..$Clients) {
        $powerShell = [PowerShell]::Create()
        $powerShell.RunspacePool = $pool
        [void]$powerShell.AddScript("& '$escapedPath' call --command=STATUS --timeout-ms=$TimeoutMs --json")
        $calls += [pscustomobject]@{
            PowerShell = $powerShell
            Handle = $powerShell.BeginInvoke()
        }
    }

    $responses = foreach ($call in $calls) {
        ($call.PowerShell.EndInvoke($call.Handle) | Out-String).Trim()
    }
    $objects = @($responses | ForEach-Object { $_ | ConvertFrom-Json })
    $ids = @($objects | ForEach-Object { $_.id })
    $successful = @($objects | Where-Object { "$($_.status)" -eq "OK" }).Count
    $uniqueIds = @($ids | Sort-Object -Unique).Count

    if ($successful -eq $Clients -and $uniqueIds -eq $Clients) {
        "concurrency=PASS clients=$Clients uniqueIds=$uniqueIds"
        exit 0
    }

    "concurrency=FAIL clients=$Clients successful=$successful uniqueIds=$uniqueIds"
    exit 1
}
finally {
    foreach ($call in $calls) { $call.PowerShell.Dispose() }
    $pool.Dispose()
}
