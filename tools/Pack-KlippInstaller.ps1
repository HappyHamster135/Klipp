[CmdletBinding()]
param(
    [Parameter(Mandatory = $true)]
    [string]$PackageDirectory,
    [ValidatePattern('^\d+\.\d+\.\d+(?:-[0-9A-Za-z.-]+)?$')]
    [string]$Version = '1.0.10',
    [string]$OutputDirectory = '',
    [string]$PreviousReleaseDirectory = '',
    [string]$VelopackToolDirectory = '',
    [string]$ReleaseNotesPath = '',
    [string]$SignTemplate = '',
    [string]$ExpectedPublisher = '',
    [switch]$RequireSigning
)

$ErrorActionPreference = 'Stop'
if ($RequireSigning -and [string]::IsNullOrWhiteSpace($SignTemplate)) {
    throw 'A production signing command is required. Configure an approved signing provider before building a signed release.'
}
if ($SignTemplate -and (!$ExpectedPublisher -or ([regex]::Matches($SignTemplate, '\{\{file(?:\.\.\.)?\}\}')).Count -ne 1)) {
    throw 'Signing requires an exact ExpectedPublisher certificate subject and one {{file}} or {{file...}} placeholder.'
}
if ($ExpectedPublisher -and !$SignTemplate) { throw 'ExpectedPublisher requires SignTemplate.' }
$repoRoot = [IO.Path]::GetFullPath((Join-Path $PSScriptRoot '..'))
$PackageDirectory = [IO.Path]::GetFullPath($PackageDirectory)
$velopackVersion = '1.2.161'
# Keep installation separate from %LOCALAPPDATA%\Klipp, where user projects and
# preferences live. Velopack owns and removes only its distinct installation.
$packageId = 'HappyHamster135.Klipp'
$channel = 'win'
foreach ($requiredFile in @('Klipp.Desktop.exe', 'Velopack.dll', 'release-manifest.json')) {
    if (!(Test-Path -LiteralPath (Join-Path $PackageDirectory $requiredFile))) {
        throw "The self-contained application publish is missing $requiredFile."
    }
}
$releaseManifest = Get-Content -LiteralPath (Join-Path $PackageDirectory 'release-manifest.json') -Raw | ConvertFrom-Json
if ($releaseManifest.version -ne $Version) { throw 'Installer and application publish versions must match.' }

if (!$OutputDirectory) { $OutputDirectory = Join-Path ([IO.Path]::GetDirectoryName($PackageDirectory)) 'installer' }
$OutputDirectory = [IO.Path]::GetFullPath($OutputDirectory)
if ($OutputDirectory.Equals($PackageDirectory, [StringComparison]::OrdinalIgnoreCase) -or
    $OutputDirectory.StartsWith($PackageDirectory.TrimEnd('\') + '\', [StringComparison]::OrdinalIgnoreCase)) {
    throw 'Installer output must be outside the application publish directory.'
}
if (Test-Path -LiteralPath $OutputDirectory) { throw 'Installer output must be a new directory.' }
New-Item -ItemType Directory -Path $OutputDirectory | Out-Null

$releaseNotes = & (Join-Path $PSScriptRoot 'New-KlippReleaseNotes.ps1') -Version $Version `
    -OutputPath (Join-Path ([IO.Path]::GetDirectoryName($OutputDirectory)) 'ReleaseNotes.md') -SourcePath $ReleaseNotesPath

if (!$VelopackToolDirectory) { $VelopackToolDirectory = Join-Path $repoRoot "artifacts/tools/vpk/$velopackVersion" }
$VelopackToolDirectory = [IO.Path]::GetFullPath($VelopackToolDirectory)
$vpk = Join-Path $VelopackToolDirectory 'vpk.exe'
if (!(Test-Path -LiteralPath $vpk)) {
    & dotnet tool install vpk --version $velopackVersion --tool-path $VelopackToolDirectory | Out-Host
    if ($LASTEXITCODE -ne 0) { throw 'Could not install the pinned local Velopack packaging tool.' }
}
$toolList = & dotnet tool list --tool-path $VelopackToolDirectory
if ($LASTEXITCODE -ne 0 -or !($toolList -match ('^vpk\s+' + [regex]::Escape($velopackVersion) + '\s'))) {
    throw "The local vpk tool must be version $velopackVersion, matching the application's Velopack package."
}

$deltaMode = 'None'
if ($PreviousReleaseDirectory) {
    $PreviousReleaseDirectory = [IO.Path]::GetFullPath($PreviousReleaseDirectory)
    if (!(Test-Path -LiteralPath $PreviousReleaseDirectory -PathType Container)) { throw 'Previous release directory was not found.' }
    $previousPackages = @(Get-ChildItem -LiteralPath $PreviousReleaseDirectory -File -Filter "$packageId-*-full.nupkg")
    if (!$previousPackages.Count) { throw 'Previous release directory has no matching full update package.' }
    foreach ($previousPackage in $previousPackages) {
        Copy-Item -LiteralPath $previousPackage.FullName -Destination (Join-Path $OutputDirectory $previousPackage.Name)
    }
    foreach ($feedName in @("releases.$channel.json", 'RELEASES')) {
        $previousFeed = Join-Path $PreviousReleaseDirectory $feedName
        if (Test-Path -LiteralPath $previousFeed) { Copy-Item -LiteralPath $previousFeed -Destination (Join-Path $OutputDirectory $feedName) }
    }
    $deltaMode = 'BestSpeed'
}

$packArguments = @(
    '--skip-updates', '--legacyConsole', '--yes', 'pack',
    '--packId', $packageId, '--packVersion', $Version,
    '--packTitle', 'Klipp', '--packAuthors', 'HappyHamster135',
    '--packDir', $PackageDirectory, '--mainExe', 'Klipp.Desktop.exe',
    '--runtime', 'win-x64', '--channel', $channel,
    '--outputDir', $OutputDirectory, '--delta', $deltaMode,
    '--icon', (Join-Path $repoRoot 'src/Klipp.Desktop/Assets/Klipp.ico'),
    '--splashImage', (Join-Path $repoRoot 'src/Klipp.Desktop/Assets/SplashScreen.scale-200.png'),
    '--splashProgressColor', '#8EAED0',
    '--aumid', $packageId, '--shortcuts', 'Desktop,StartMenuRoot'
)
if ($releaseNotes) { $packArguments += @('--releaseNotes', $releaseNotes.Path) }
if ($SignTemplate) {
    # Only Klipp binaries and installer helpers are submitted to our provider.
    # Upstream libraries retain their existing signatures. Foundation approval
    # must explicitly include the Velopack launcher and updater helpers.
    $signFiles = @('Klipp.Desktop.exe', 'Klipp.Desktop.dll', 'Klipp.Core.dll', 'Klipp.Storage.dll',
        'Klipp.Capture.dll', 'Klipp.Encoding.dll', 'Klipp.AudioEffects.dll', 'Klipp.GameHook.dll',
        'Klipp_ExecutionStub.exe', 'Squirrel.exe', 'Update.exe',
        'HappyHamster135.Klipp-win-Setup.exe', 'Klipp-Setup.exe')
    $signExclude = '(?i)(?:^|[\\/])(?!(?:' + (($signFiles | ForEach-Object { [regex]::Escape($_) }) -join '|') + ')$)[^\\/]+$'
    $packArguments += @('--signTemplate', $SignTemplate, '--signExclude', $signExclude)
}
else { $packArguments += '--noPortable' }
Write-Host "[Klipp] Creating the per-user installer and GitHub update packages ($Version)..."
& $vpk @packArguments | Out-Host
if ($LASTEXITCODE -ne 0) { throw "Velopack packaging failed ($LASTEXITCODE). Staging files remain at $OutputDirectory." }

$setupFiles = @(Get-ChildItem -LiteralPath $OutputDirectory -File -Filter '*-Setup.exe')
if ($setupFiles.Count -ne 1) { throw 'Velopack did not produce exactly one Setup executable.' }
$oldSetupName = $setupFiles[0].Name
$installer = Join-Path $OutputDirectory 'Klipp-Setup.exe'
Move-Item -LiteralPath $setupFiles[0].FullName -Destination $installer
# The upload manifest also records the setup filename. Update only that exact
# JSON string; update-feed nupkg filenames remain untouched.
$assetManifest = Join-Path $OutputDirectory "assets.$channel.json"
if (Test-Path -LiteralPath $assetManifest) {
    $assetJson = [IO.File]::ReadAllText($assetManifest)
    $assetJson = $assetJson.Replace('"' + $oldSetupName + '"', '"Klipp-Setup.exe"')
    [IO.File]::WriteAllText($assetManifest, $assetJson)
}

$updateFeed = Join-Path $OutputDirectory "releases.$channel.json"
$updatePackages = @(Get-ChildItem -LiteralPath $OutputDirectory -File -Filter "$packageId-$Version-*.nupkg")
if (!(Test-Path -LiteralPath $updateFeed) -or !$updatePackages.Count) { throw 'Update feed or versioned packages are missing.' }
$feed = [IO.File]::ReadAllText($updateFeed) | ConvertFrom-Json
$currentFull = @($feed.Assets | Where-Object { $_.PackageId -eq $packageId -and $_.Version -eq $Version -and $_.Type -eq 'Full' })
if ($currentFull.Count -ne 1) { throw 'The update feed must contain one full package for this application version.' }
$fullPackage = Join-Path $OutputDirectory $currentFull[0].FileName
if (!(Test-Path -LiteralPath $fullPackage)) { throw 'The full update package referenced by the feed was not produced.' }
if ((Get-Item -LiteralPath $fullPackage).Length -ne $currentFull[0].Size) { throw 'Full package size does not match the update feed.' }
if ($currentFull[0].SHA256 -and (Get-FileHash -LiteralPath $fullPackage -Algorithm SHA256).Hash -ne $currentFull[0].SHA256) {
    throw 'Full package checksum does not match the update feed.'
}
if ($currentFull[0].SHA1 -and (Get-FileHash -LiteralPath $fullPackage -Algorithm SHA1).Hash -ne $currentFull[0].SHA1) {
    throw 'Full package checksum does not match the update feed.'
}

$expectedNotes = if ($releaseNotes) { $releaseNotes.Markdown } else { '' }
$feedNotes = [string]$currentFull[0].NotesMarkdown
if ($feedNotes.Replace("`r`n", "`n").Trim() -cne $expectedNotes) { throw "Update feed notes do not match this version's source." }
Add-Type -AssemblyName System.IO.Compression.FileSystem
$packageArchive = [IO.Compression.ZipFile]::OpenRead($fullPackage)
try {
    $nuspec = @($packageArchive.Entries | Where-Object { $_.FullName.EndsWith('.nuspec', [StringComparison]::OrdinalIgnoreCase) })
    if ($nuspec.Count -ne 1) { throw 'Expected exactly one package manifest for release notes verification.' }
    $xmlSettings = [Xml.XmlReaderSettings]::new()
    $xmlSettings.DtdProcessing = [Xml.DtdProcessing]::Prohibit
    $xmlSettings.XmlResolver = $null
    $stream = $nuspec[0].Open()
    $reader = [Xml.XmlReader]::Create($stream, $xmlSettings)
    try {
        $manifestXml = [Xml.XmlDocument]::new()
        $manifestXml.Load($reader)
        $noteNode = $manifestXml.SelectSingleNode("/*[local-name()='package']/*[local-name()='metadata']/*[local-name()='releaseNotes']")
        $packageNotes = if ($noteNode) { $noteNode.InnerText } else { '' }
        if ($packageNotes.Replace("`r`n", "`n").Trim() -cne $expectedNotes) { throw "Prepared package notes do not match this version's source." }
    }
    finally { $reader.Dispose(); $stream.Dispose() }
}
finally { $packageArchive.Dispose() }

& (Join-Path $PSScriptRoot 'Test-KlippReleaseIcons.ps1') -PackageDirectory $PackageDirectory -InstallerPath $installer -FullPackagePath $fullPackage

$releaseAssets = @($installer) + @($updatePackages | ForEach-Object FullName) + @($updateFeed)
foreach ($extraName in @("assets.$channel.json", 'RELEASES')) {
    $extraPath = Join-Path $OutputDirectory $extraName
    if (Test-Path -LiteralPath $extraPath) { $releaseAssets += $extraPath }
}
$portableArchive = $null
if ($SignTemplate) {
    # vpk signs its temporary payload before creating this ZIP and the full
    # update package. Zipping the original publish tree would be unsigned.
    $portableFiles = @(Get-ChildItem -LiteralPath $OutputDirectory -File -Filter '*-Portable.zip')
    if ($portableFiles.Count -ne 1) { throw 'The signed portable archive was not produced.' }
    $portableArchive = Join-Path $OutputDirectory "Klipp-$Version-win-x64.zip"
    Move-Item -LiteralPath $portableFiles[0].FullName -Destination $portableArchive
    if (Test-Path -LiteralPath $assetManifest) {
        $assetJson = [IO.File]::ReadAllText($assetManifest).Replace('"' + $portableFiles[0].Name + '"', '"' + [IO.Path]::GetFileName($portableArchive) + '"')
        [IO.File]::WriteAllText($assetManifest, $assetJson)
    }
    $verificationManifest = Join-Path $OutputDirectory '.signing-verification.json'
    [ordered]@{
        version = $Version; installer = $installer; updateFeed = $updateFeed
        releaseAssets = @(@($releaseAssets) + @($portableArchive) | ForEach-Object {
            [ordered]@{ path = $_; name = [IO.Path]::GetFileName($_); size = (Get-Item -LiteralPath $_).Length
                sha256 = (Get-FileHash -LiteralPath $_ -Algorithm SHA256).Hash.ToLowerInvariant() }
        })
    } | ConvertTo-Json -Depth 5 | Set-Content -LiteralPath $verificationManifest -Encoding utf8
    & (Join-Path $PSScriptRoot 'Test-KlippReleaseSignatures.ps1') -ReleaseManifestPath $verificationManifest -ExpectedPublisher $ExpectedPublisher | Out-Null
}
[pscustomobject]@{
    PackageId = $packageId
    Channel = $channel
    Installer = $installer
    UpdateFeed = $updateFeed
    UpdatePackages = @($updatePackages | ForEach-Object FullName)
    PortableArchive = $portableArchive
    ReleaseAssets = $releaseAssets
    ReleaseNotesPath = $releaseNotes.Path
}
