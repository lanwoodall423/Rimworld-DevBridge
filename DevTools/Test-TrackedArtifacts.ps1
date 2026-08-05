$ErrorActionPreference = 'Stop'
$root = [IO.Path]::GetFullPath((Split-Path -Parent $PSScriptRoot))
$forbidden = @(
    '(^|/)(bin|obj)(/|$)',
    '(?i)\.(dll|exe|pdb|zip|nupkg|dmp)$',
    '(?i)(Assembly-CSharp|UnityEngine|0Harmony|Harmony|RimWorldWin|ISharpZipLib|NAudio|NVorbis|Steamworks)'
)
$tracked = @(git -C $root ls-files)
$violations = New-Object 'Collections.Generic.List[string]'
foreach ($path in $tracked) {
    foreach ($pattern in $forbidden) {
        if ($path -match $pattern) { $violations.Add($path); break }
    }
}
if ($violations.Count -gt 0) { throw ('Tracked generated/proprietary artifacts found: ' + (($violations | Sort-Object -Unique) -join ', ')) }
Write-Output ('trackedArtifactCheck=PASS trackedFiles={0}' -f $tracked.Count)
