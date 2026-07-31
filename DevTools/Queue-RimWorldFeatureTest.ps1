param(
    [Parameter(Mandatory=$true)][string]$Mod,
    [Parameter(Mandatory=$true)][string]$Feature,
    [Parameter(Mandatory=$true)][string]$Test,
    [Parameter(Mandatory=$true)][string]$Command,
    [string]$Argument = "",
    [string[]]$ExpectContains = @(),
    [string[]]$RejectContains = @(),
    [string]$ExpectStatus = "OK",
    [string[]]$RequiredMods = @(),
    [string[]]$RequiredAdapters = @(),
    [string]$RequiredSave = "",
    [string]$Mutation = "",
    [ValidateRange(0,120000)][int]$TimeoutMs = 0,
    [ValidateRange(0,2147483647)][int]$TickBudget = 0,
    [Nullable[int]]$RandomSeed = $null
)

$saveDir = Join-Path $env:USERPROFILE "AppData\LocalLow\Ludeon Studios\RimWorld by Ludeon Studios"
$root = Join-Path $saveDir "RimWorldDevBridge\FeatureTests\Pending"
[IO.Directory]::CreateDirectory($root) | Out-Null
$id = [Guid]::NewGuid().ToString("N")
$finalPath = Join-Path $root ($id + ".xml")
$tempPath = $finalPath + ".tmp"

$settings = [Xml.XmlWriterSettings]::new()
$settings.Indent = $true
$settings.Encoding = [Text.UTF8Encoding]::new($false)
$writer = [Xml.XmlWriter]::Create($tempPath, $settings)
try {
    $writer.WriteStartDocument()
    $writer.WriteStartElement("FeatureTestSuite")
    $writer.WriteAttributeString("id", $id)
    $writer.WriteAttributeString("mod", $Mod)
    $writer.WriteAttributeString("feature", $Feature)
    $writer.WriteAttributeString("queuedUtc", [DateTime]::UtcNow.ToString("s") + "Z")
    if ($RequiredMods.Count -gt 0) { $writer.WriteAttributeString("requiredMods", ($RequiredMods -join ",")) }
    if ($RequiredAdapters.Count -gt 0) { $writer.WriteAttributeString("requiredAdapters", ($RequiredAdapters -join ",")) }
    if ($RequiredSave.Length -gt 0) { $writer.WriteAttributeString("requiredSave", $RequiredSave) }
    $writer.WriteStartElement("Test")
    $writer.WriteAttributeString("name", $Test)
    if ($Mutation.Length -gt 0) { $writer.WriteAttributeString("mutation", $Mutation) }
    if ($TimeoutMs -gt 0) { $writer.WriteAttributeString("timeoutMs", "$TimeoutMs") }
    if ($TickBudget -gt 0) { $writer.WriteAttributeString("tickBudget", "$TickBudget") }
    if ($null -ne $RandomSeed) { $writer.WriteAttributeString("randomSeed", "$RandomSeed") }
    $writer.WriteStartElement("Action")
    $writer.WriteStartElement("Call")
    $writer.WriteAttributeString("id", "action")
    $writer.WriteAttributeString("command", $Command.ToUpperInvariant())
    if ($Argument.Length -gt 0) { $writer.WriteAttributeString("argument", $Argument) }
    $writer.WriteEndElement()
    $writer.WriteEndElement()
    $writer.WriteStartElement("Assertions")
    if ($ExpectStatus.Length -gt 0) {
        $writer.WriteStartElement("Status")
        $writer.WriteAttributeString("step", "action")
        $writer.WriteAttributeString("value", $ExpectStatus.ToUpperInvariant())
        $writer.WriteEndElement()
    }
    foreach ($value in $ExpectContains) {
        $writer.WriteStartElement("Contains")
        $writer.WriteAttributeString("step", "action")
        $writer.WriteAttributeString("value", $value)
        $writer.WriteEndElement()
    }
    foreach ($value in $RejectContains) {
        $writer.WriteStartElement("NotContains")
        $writer.WriteAttributeString("step", "action")
        $writer.WriteAttributeString("value", $value)
        $writer.WriteEndElement()
    }
    $writer.WriteEndElement()
    $writer.WriteEndElement()
    $writer.WriteEndElement()
    $writer.WriteEndDocument()
}
finally {
    $writer.Dispose()
}
[IO.File]::Move($tempPath, $finalPath)
Write-Output ("queued={0} mod:{1} feature:{2} test:{3}" -f $id, $Mod, $Feature, $Test)
