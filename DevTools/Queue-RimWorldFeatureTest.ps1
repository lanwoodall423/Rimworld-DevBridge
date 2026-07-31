param(
    [Parameter(Mandatory=$true)][string]$Mod,
    [Parameter(Mandatory=$true)][string]$Feature,
    [Parameter(Mandatory=$true)][string]$Test,
    [Parameter(Mandatory=$true)][string]$Command,
    [string]$Argument = "",
    [string[]]$ExpectContains = @(),
    [string[]]$RejectContains = @()
)

$root = Join-Path (Split-Path -Parent $PSScriptRoot) "DevTools\FeatureTests\Pending"
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
    $writer.WriteStartElement("Test")
    $writer.WriteAttributeString("name", $Test)
    $writer.WriteAttributeString("command", $Command.ToUpperInvariant())
    if ($Argument.Length -gt 0) { $writer.WriteAttributeString("argument", $Argument) }
    foreach ($value in $ExpectContains) {
        $writer.WriteStartElement("Expect")
        $writer.WriteAttributeString("contains", $value)
        $writer.WriteEndElement()
    }
    foreach ($value in $RejectContains) {
        $writer.WriteStartElement("Reject")
        $writer.WriteAttributeString("contains", $value)
        $writer.WriteEndElement()
    }
    $writer.WriteEndElement()
    $writer.WriteEndElement()
    $writer.WriteEndDocument()
}
finally {
    $writer.Dispose()
}
[IO.File]::Move($tempPath, $finalPath)
Write-Output ("queued={0} mod:{1} feature:{2} test:{3}" -f $id, $Mod, $Feature, $Test)
