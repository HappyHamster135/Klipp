[CmdletBinding()]
param(
    [string]$TrustedFixtureBinaryPath = '',
    [string]$UnsignedReleaseManifestPath = ''
)

# Uses existing signed Microsoft bytes for positive verification fixtures. It
# creates no signing keys, changes no certificate stores and executes no binary.
$ErrorActionPreference = 'Stop'
Add-Type -AssemblyName System.IO.Compression
Add-Type -AssemblyName System.IO.Compression.FileSystem
$verifier = Join-Path $PSScriptRoot 'Test-KlippReleaseSignatures.ps1'
$qaRoot = Join-Path ([IO.Path]::GetTempPath()) ('Klipp-Signature-Tests-' + [Guid]::NewGuid().ToString('N'))
[void][IO.Directory]::CreateDirectory($qaRoot)
$binaries = @('Klipp.Desktop.exe', 'Klipp.Desktop.dll', 'Klipp.Core.dll', 'Klipp.Capture.dll',
    'Klipp.Encoding.dll', 'Klipp.Storage.dll', 'Klipp.AudioEffects.dll', 'Klipp.GameHook.dll')
$script:checkCount = 0

function Write-TestZip([string]$Path, [string[]]$Names, [byte[]]$Bytes) {
    $zip = [IO.Compression.ZipFile]::Open($Path, [IO.Compression.ZipArchiveMode]::Create)
    try {
        foreach ($name in $Names) {
            $entry = $zip.CreateEntry($name)
            $stream = $entry.Open()
            try { $stream.Write($Bytes, 0, $Bytes.Length) } finally { $stream.Dispose() }
        }
    }
    finally { $zip.Dispose() }
}

function New-TestRelease([string[]]$PortableNames = @(), [string[]]$UpdateNames = @(), [byte[]]$Bytes = @(77, 90)) {
    $root = Join-Path $qaRoot ([Guid]::NewGuid().ToString('N'))
    [void][IO.Directory]::CreateDirectory($root)
    if (!$PortableNames.Count) { $PortableNames = @($binaries | ForEach-Object { "Klipp-2.0.0-win-x64/$_" }) }
    if (!$UpdateNames.Count) { $UpdateNames = @($binaries + @('Klipp_ExecutionStub.exe', 'Squirrel.exe') | ForEach-Object { "lib/app/$_" }) }
    $setup = Join-Path $root 'Klipp-Setup.exe'
    [IO.File]::WriteAllBytes($setup, $Bytes)
    $portable = Join-Path $root 'Klipp-2.0.0-win-x64.zip'
    $package = Join-Path $root 'HappyHamster135.Klipp-2.0.0-full.nupkg'
    Write-TestZip $portable $PortableNames $Bytes
    Write-TestZip $package $UpdateNames $Bytes
    $feedPath = Join-Path $root 'releases.win.json'
    [ordered]@{Assets = @([ordered]@{
        PackageId = 'HappyHamster135.Klipp'; Version = '2.0.0'; Type = 'Full'
        FileName = [IO.Path]::GetFileName($package); Size = (Get-Item -LiteralPath $package).Length
        SHA256 = (Get-FileHash -LiteralPath $package -Algorithm SHA256).Hash
        SHA1 = (Get-FileHash -LiteralPath $package -Algorithm SHA1).Hash
    })} | ConvertTo-Json -Depth 5 | Set-Content -LiteralPath $feedPath -Encoding utf8
    $manifest = [pscustomobject]@{
        version = '2.0.0'; installer = $setup; updateFeed = $feedPath
        releaseAssets = @(@($setup, $portable, $package, $feedPath) | ForEach-Object {
            [pscustomobject]@{path = $_; name = [IO.Path]::GetFileName($_)
                size = (Get-Item -LiteralPath $_).Length; sha256 = (Get-FileHash -LiteralPath $_ -Algorithm SHA256).Hash}
        })
    }
    $path = Join-Path $root 'release-artifacts.json'
    $manifest | ConvertTo-Json -Depth 6 | Set-Content -LiteralPath $path -Encoding utf8
    return [pscustomobject]@{Root = $root; Path = $path; Manifest = $manifest}
}

function Assert-Rejected([string]$Name, [string]$ManifestPath, [string]$Message, [string]$Publisher = '') {
    $failure = $null
    try { & $verifier -ReleaseManifestPath $ManifestPath -ExpectedPublisher $Publisher | Out-Null }
    catch { $failure = $_.Exception.Message }
    if (!$failure -or $failure -notlike $Message) { throw "Check failed: $Name. Expected '$Message'; got '$failure'." }
    $script:checkCount++
    Write-Output "PASS $Name"
}

$fixture = New-TestRelease
Assert-Rejected 'unsigned payload' $fixture.Path 'Signature verification failed*'
$fixture.Manifest.releaseAssets[0].sha256 = '0' * 64
$fixture.Manifest | ConvertTo-Json -Depth 6 | Set-Content -LiteralPath $fixture.Path -Encoding utf8
Assert-Rejected 'modified asset' $fixture.Path '*size or SHA-256 mismatch*'

$fixture = New-TestRelease
$fixture.Manifest.releaseAssets += $fixture.Manifest.releaseAssets[0]
$fixture.Manifest | ConvertTo-Json -Depth 6 | Set-Content -LiteralPath $fixture.Path -Encoding utf8
Assert-Rejected 'duplicate asset name' $fixture.Path '*unique basenames*'

$fixture = New-TestRelease
$fixture.Manifest.releaseAssets[0].path = Join-Path $qaRoot 'outside.exe'
[IO.File]::WriteAllBytes($fixture.Manifest.releaseAssets[0].path, @(77, 90))
$fixture.Manifest | ConvertTo-Json -Depth 6 | Set-Content -LiteralPath $fixture.Path -Encoding utf8
Assert-Rejected 'asset outside release' $fixture.Path '*stay within the release directory*'

$fixture = New-TestRelease
$feed = Get-Content -LiteralPath $fixture.Manifest.updateFeed -Raw | ConvertFrom-Json
$feed.Assets[0].SHA256 = '0' * 64
$feed | ConvertTo-Json -Depth 5 | Set-Content -LiteralPath $fixture.Manifest.updateFeed -Encoding utf8
$feedAsset = $fixture.Manifest.releaseAssets | Where-Object name -EQ 'releases.win.json'
$feedAsset.size = (Get-Item -LiteralPath $fixture.Manifest.updateFeed).Length
$feedAsset.sha256 = (Get-FileHash -LiteralPath $fixture.Manifest.updateFeed -Algorithm SHA256).Hash
$fixture.Manifest | ConvertTo-Json -Depth 6 | Set-Content -LiteralPath $fixture.Path -Encoding utf8
Assert-Rejected 'feed hash mismatch' $fixture.Path '*does not match its feed size or SHA-256*'

foreach ($unsafe in @('../outside.dll', '/absolute.dll', 'C:/absolute.dll', 'safe/../outside.dll')) {
    $names = @($binaries | ForEach-Object { "Klipp-2.0.0-win-x64/$_" }) + $unsafe
    $fixture = New-TestRelease -PortableNames $names
    Assert-Rejected "unsafe archive entry $unsafe" $fixture.Path '*Unsafe or duplicate ZIP entry*'
}

$names = @($binaries | ForEach-Object { "Klipp-2.0.0-win-x64/$_" }) + 'Klipp-2.0.0-win-x64/Klipp.Core.dll'
$fixture = New-TestRelease -PortableNames $names
Assert-Rejected 'duplicate archive entry' $fixture.Path '*Unsafe or duplicate ZIP entry*'
$names = @($binaries | ForEach-Object { "Klipp-2.0.0-win-x64/$_" }) + 'Klipp.Core.dll'
$fixture = New-TestRelease -PortableNames $names
Assert-Rejected 'colliding binary basename' $fixture.Path '*duplicate binary basename*'
$names = @($binaries | Where-Object { $_ -ne 'Klipp.GameHook.dll' } | ForEach-Object { "Klipp-2.0.0-win-x64/$_" })
$fixture = New-TestRelease -PortableNames $names
Assert-Rejected 'missing first-party binary' $fixture.Path '*Missing required binary*Klipp.GameHook.dll*'
$names = @($binaries + 'Squirrel.exe' | ForEach-Object { "lib/app/$_" })
$fixture = New-TestRelease -UpdateNames $names
Assert-Rejected 'missing update stub' $fixture.Path '*signed execution stub and updater*'
$names = @($binaries | ForEach-Object { "current/$_" }) + 'Klipp.exe'
$fixture = New-TestRelease -PortableNames $names
Assert-Rejected 'missing portable updater' $fixture.Path '*signed execution stub and updater*'

if ($TrustedFixtureBinaryPath) {
    $bytes = [IO.File]::ReadAllBytes((Resolve-Path -LiteralPath $TrustedFixtureBinaryPath).Path)
    $publisher = (Get-AuthenticodeSignature -LiteralPath $TrustedFixtureBinaryPath).SignerCertificate.Subject
    foreach ($layout in @('original', 'velopack')) {
        $names = if ($layout -eq 'velopack') { @($binaries | ForEach-Object { "current/$_" }) + @('Klipp.exe', 'Update.exe') }
            else { @($binaries | ForEach-Object { "Klipp-2.0.0-win-x64/$_" }) }
        $fixture = New-TestRelease -PortableNames $names -Bytes $bytes
        $result = & $verifier -ReleaseManifestPath $fixture.Path -ExpectedPublisher $publisher
        $expectedCount = if ($layout -eq 'velopack') { 21 } else { 19 }
        if ($result.VerifiedBinaries -ne $expectedCount) { throw "Wrong verified count for $layout layout." }
        $script:checkCount++
        Write-Output "PASS existing trusted timestamped bytes, $layout layout"
    }
    Assert-Rejected 'wrong expected publisher' $fixture.Path '*Unexpected signing publisher*' 'CN=Unrelated publisher'
    foreach ($asset in $fixture.Manifest.releaseAssets) { $asset.path = $asset.name }
    $fixture.Manifest.installer = 'Klipp-Setup.exe'
    $fixture.Manifest.updateFeed = 'releases.win.json'
    $fixture.Manifest | ConvertTo-Json -Depth 6 | Set-Content -LiteralPath $fixture.Path -Encoding utf8
    $result = & $verifier -ReleaseManifestPath $fixture.Path -ExpectedPublisher $publisher
    if ($result.VerifiedBinaries -ne 21) { throw 'Relative asset paths were not verified.' }
    $script:checkCount++
    Write-Output 'PASS relative manifest asset paths'
}
if ($UnsignedReleaseManifestPath) {
    Assert-Rejected 'actual unsigned release rejected' $UnsignedReleaseManifestPath 'Signature verification failed (NotSigned): installer/Klipp-Setup.exe'
}
Write-Output "$script:checkCount release signature checks passed. Fixtures: $qaRoot"
