[CmdletBinding()]
param(
    [ValidatePattern('^\d+\.\d+\.\d+(?:-[0-9A-Za-z.-]+)?$')]
    [string]$Version = '1.0.6',
    [string]$OutputRoot = '',
    [string]$VisualStudioDirectory = '',
    [string]$VcRuntimeDirectory = '',
    [string]$PreviousReleaseDirectory = '',
    [switch]$PortableOnly
)

$ErrorActionPreference = 'Stop'
$repoRoot = [IO.Path]::GetFullPath((Join-Path $PSScriptRoot '..'))
if (!$OutputRoot) { $OutputRoot = Join-Path $repoRoot 'artifacts/releases' }
$OutputRoot = [IO.Path]::GetFullPath($OutputRoot)
if (!(Get-Command dotnet -ErrorAction SilentlyContinue)) { throw '.NET 10 SDK is required.' }

$vswhere = Join-Path ${env:ProgramFiles(x86)} 'Microsoft Visual Studio/Installer/vswhere.exe'
if (!$VisualStudioDirectory) {
    if (!(Test-Path -LiteralPath $vswhere)) { throw 'Visual Studio with Windows App SDK and C++ build tools is required.' }
    $VisualStudioDirectory = & $vswhere -latest -prerelease -products '*' -requires Microsoft.VisualStudio.Component.VC.Tools.x86.x64 -property installationPath | Select-Object -First 1
}
if (!$VisualStudioDirectory -or !(Test-Path -LiteralPath $VisualStudioDirectory)) { throw 'Visual Studio C++ build tools were not found.' }
$VisualStudioDirectory = [IO.Path]::GetFullPath($VisualStudioDirectory)
$packagingTask = Get-ChildItem -LiteralPath (Join-Path $VisualStudioDirectory 'MSBuild/Microsoft/VisualStudio') -Filter 'Microsoft.Build.Packaging.Pri.Tasks.dll' -File -Recurse |
    Sort-Object FullName -Descending | Select-Object -First 1
if (!$packagingTask) { throw 'Install the Windows App SDK development workload: PRI packaging tools were not found.' }
$packagingTools = $packagingTask.Directory.FullName + '/'

# These are Microsoft's redistributable release DLLs, never the debug CRT or System32 copies.
if (!$VcRuntimeDirectory) {
    $redistRoot = Join-Path $VisualStudioDirectory 'VC/Redist/MSVC'
    $redistVersions = @(Get-ChildItem -LiteralPath $redistRoot -Directory | Where-Object { $_.Name -match '^\d+\.\d+\.\d+$' } |
        Sort-Object { [version]$_.Name } -Descending)
    foreach ($redistVersion in $redistVersions) {
        $architectureRoot = Join-Path $redistVersion.FullName 'x64'
        if (!(Test-Path -LiteralPath $architectureRoot)) { continue }
        $crt = Get-ChildItem -LiteralPath $architectureRoot -Directory -Filter 'Microsoft.VC*.CRT' | Select-Object -First 1
        if ($crt) { $VcRuntimeDirectory = $crt.FullName; break }
    }
}
if (!$VcRuntimeDirectory -or !(Test-Path -LiteralPath $VcRuntimeDirectory)) { throw 'The Visual Studio x64 VC++ release redistributable directory was not found.' }
foreach ($requiredCrt in @('msvcp140.dll', 'vcruntime140.dll', 'vcruntime140_1.dll')) {
    if (!(Test-Path -LiteralPath (Join-Path $VcRuntimeDirectory $requiredCrt))) { throw "VC++ redistributable is missing $requiredCrt." }
}

# Every run gets a new directory. Existing releases, source files and user data are never removed.
$runName = $Version + '-' + (Get-Date -Format 'yyyyMMdd-HHmmss') + '-' + [Guid]::NewGuid().ToString('N').Substring(0, 8)
$runDirectory = Join-Path $OutputRoot $runName
$packageName = "Klipp-$Version-win-x64"
$packageDirectory = Join-Path $runDirectory $packageName
if (Test-Path -LiteralPath $runDirectory) { throw 'Release output directory already exists.' }
New-Item -ItemType Directory -Path $packageDirectory | Out-Null

$publishArguments = @(
    'publish', (Join-Path $repoRoot 'src/Klipp.Desktop/Klipp.Desktop.csproj'),
    '-c', 'Release', '-r', 'win-x64', '-p:Platform=x64',
    '-p:WindowsPackageType=None', '-p:SelfContained=true',
    '-p:WindowsAppSDKSelfContained=true', '-p:PublishTrimmed=false',
    '-p:PublishSingleFile=false', '-p:KlippDevSigningEnabled=false',
    '-p:DebugType=None', '-p:DebugSymbols=false', '-p:CopyOutputSymbolsToPublishDirectory=false',
    "-p:Version=$Version", "-p:AppxMSBuildToolsPath=$packagingTools", '-o', $packageDirectory
)
Write-Host "[Klipp] Publishing Release x64 $Version..."
& dotnet @publishArguments
if ($LASTEXITCODE -ne 0) { throw "Release publish failed ($LASTEXITCODE). Staging files remain at $runDirectory." }

foreach ($runtimeFile in Get-ChildItem -LiteralPath $VcRuntimeDirectory -File -Filter '*.dll') {
    Copy-Item -LiteralPath $runtimeFile.FullName -Destination (Join-Path $packageDirectory $runtimeFile.Name) -Force
}
Copy-Item -LiteralPath (Join-Path $repoRoot 'LICENSE') -Destination (Join-Path $packageDirectory 'LICENSE')
Copy-Item -LiteralPath (Join-Path $repoRoot 'src/Klipp.GameHook/third_party/minhook/LICENSE.txt') -Destination (Join-Path $packageDirectory 'MinHook.LICENSE.txt') -Force

# Keep dependency copyright and license notices with the distributed binaries.
$licenseDirectory = Join-Path $packageDirectory 'ThirdPartyLicenses'
New-Item -ItemType Directory -Path $licenseDirectory | Out-Null
$assets = Get-Content -LiteralPath (Join-Path $repoRoot 'src/Klipp.Desktop/obj/project.assets.json') -Raw | ConvertFrom-Json
$notices = [Collections.Generic.List[string]]::new()
$notices.Add('Third-party packages used by Klipp. Package license files are in ThirdPartyLicenses.')
foreach ($library in $assets.libraries.PSObject.Properties | Sort-Object Name) {
    if ($library.Value.type -ne 'package') { continue }
    $packagePath = $null
    foreach ($packageFolder in $assets.packageFolders.PSObject.Properties) {
        $candidate = Join-Path $packageFolder.Name $library.Value.path
        if (Test-Path -LiteralPath $candidate) { $packagePath = [IO.Path]::GetFullPath($candidate); break }
    }
    if (!$packagePath) { throw "Cannot find license metadata for $($library.Name)." }
    $nuspecFile = Get-ChildItem -LiteralPath $packagePath -File -Filter '*.nuspec' | Select-Object -First 1
    if (!$nuspecFile) { throw "Cannot find package metadata for $($library.Name)." }
    [xml]$nuspec = Get-Content -LiteralPath $nuspecFile.FullName -Raw
    $metadata = $nuspec.package.metadata
    $notices.Add('')
    $notices.Add($library.Name)
    $notices.Add('Authors: ' + [string]$metadata.authors)
    $notices.Add([string]$metadata.copyright)
    $notices.Add('License: ' + [string]$metadata.license.'#text')
    $notices.Add('License URL: ' + [string]$metadata.licenseUrl)
    $packageLicenseDirectory = Join-Path $licenseDirectory ($library.Name -replace '[/\\]', '-')
    New-Item -ItemType Directory -Path $packageLicenseDirectory | Out-Null
    Copy-Item -LiteralPath $nuspecFile.FullName -Destination $packageLicenseDirectory
    foreach ($noticeFile in Get-ChildItem -LiteralPath $packagePath -File | Where-Object { $_.Name -match '^(LICENSE|NOTICE|THIRDPARTY)' }) {
        Copy-Item -LiteralPath $noticeFile.FullName -Destination $packageLicenseDirectory
    }
    if ($metadata.license.type -eq 'file') {
        $licensePath = [IO.Path]::GetFullPath((Join-Path $packagePath ([string]$metadata.license.'#text')))
        if (!$licensePath.StartsWith($packagePath.TrimEnd('\') + '\', [StringComparison]::OrdinalIgnoreCase)) { throw 'Invalid package license path.' }
        Copy-Item -LiteralPath $licensePath -Destination $packageLicenseDirectory -Force
    }
}
$notices.Add(@'

MIT license text for the packages marked MIT above:

Permission is hereby granted, free of charge, to any person obtaining a copy of
this software and associated documentation files (the "Software"), to deal in
the Software without restriction, including without limitation the rights to
use, copy, modify, merge, publish, distribute, sublicense, and/or sell copies of
the Software, and to permit persons to whom the Software is furnished to do so,
subject to the following conditions:

The above copyright notice and this permission notice shall be included in all
copies or substantial portions of the Software.

THE SOFTWARE IS PROVIDED "AS IS", WITHOUT WARRANTY OF ANY KIND, EXPRESS OR
IMPLIED, INCLUDING BUT NOT LIMITED TO THE WARRANTIES OF MERCHANTABILITY,
FITNESS FOR A PARTICULAR PURPOSE AND NONINFRINGEMENT. IN NO EVENT SHALL THE
AUTHORS OR COPYRIGHT HOLDERS BE LIABLE FOR ANY CLAIM, DAMAGES OR OTHER
LIABILITY, WHETHER IN AN ACTION OF CONTRACT, TORT OR OTHERWISE, ARISING FROM,
OUT OF OR IN CONNECTION WITH THE SOFTWARE OR THE USE OR OTHER DEALINGS IN THE
SOFTWARE.

Microsoft Visual C++ runtime redistribution information:
https://learn.microsoft.com/en-us/cpp/windows/redistributing-visual-cpp-files
'@)
$notices | Set-Content -LiteralPath (Join-Path $packageDirectory 'THIRD-PARTY-NOTICES.txt') -Encoding utf8
$dotnetDirectory = [IO.Path]::GetDirectoryName((Get-Command dotnet).Source)
foreach ($dotnetNotice in @('LICENSE.txt', 'ThirdPartyNotices.txt')) {
    $dotnetNoticePath = Join-Path $dotnetDirectory $dotnetNotice
    if (Test-Path -LiteralPath $dotnetNoticePath) { Copy-Item -LiteralPath $dotnetNoticePath -Destination (Join-Path $licenseDirectory ('dotnet-' + $dotnetNotice)) }
}

$requiredFiles = @(
    'Klipp.Desktop.exe', 'Klipp.Desktop.dll', 'Klipp.AudioEffects.dll', 'Klipp.GameHook.dll',
    'coreclr.dll', 'Microsoft.UI.Xaml.dll', 'resources.pri',
    'msvcp140.dll', 'vcruntime140.dll', 'vcruntime140_1.dll', 'LICENSE', 'MinHook.LICENSE.txt'
)
foreach ($requiredFile in $requiredFiles) {
    if (!(Test-Path -LiteralPath (Join-Path $packageDirectory $requiredFile))) { throw "Release is missing $requiredFile." }
}
$unexpected = @(Get-ChildItem -LiteralPath $packageDirectory -File -Recurse | Where-Object {
    $_.Name -match 'Harness|\.(pfx|p12|key|pem|mp4|wav|raw)$' -or
    $_.Name -in @('settings.json', 'playback-positions.json', 'crash.log', 'InstallKlippDevSigningCertificate.ps1')
})
if ($unexpected.Count) { throw ('Unexpected private/development files in publish output: ' + ($unexpected.Name -join ', ')) }

$sourceCommit = ''
if (Get-Command git -ErrorAction SilentlyContinue) {
    $sourceCommit = (& git -C $repoRoot rev-parse HEAD 2>$null | Select-Object -First 1)
}
[ordered]@{
    product = 'Klipp'; version = $Version; architecture = 'win-x64'
    sourceCommit = $sourceCommit; builtUtc = [DateTime]::UtcNow.ToString('o')
    dotnetSelfContained = $true; windowsAppSdkSelfContained = $true; trimmed = $false
} | ConvertTo-Json | Set-Content -LiteralPath (Join-Path $packageDirectory 'release-manifest.json') -Encoding utf8

@"
Klipp $Version for Windows x64

Extract this entire folder and run Klipp.Desktop.exe. Keep the DLLs and folders
beside the executable. No .NET or Windows App Runtime installation is required.
The Microsoft VC++ release runtime is included locally.

Requires Windows 10 version 2004 (build 19041) or newer and a DirectX 11-capable GPU.
Game-only and separate app audio require Windows build 20348 or newer.
The app downloads FFmpeg/FFprobe on first use; captions download whisper.cpp and
its model only when requested. Those tools are not part of this archive.

Settings and caches: %LOCALAPPDATA%\Klipp
Default recordings: %USERPROFILE%\Videos\Klipp

For automatic updates, use Klipp-Setup.exe from the same GitHub release. The
installer uses its own application folder and preserves these data folders.
This portable release is unsigned. It does not install developer certificates.
Source, license information and updates: https://github.com/HappyHamster135/Klipp
"@ | Set-Content -LiteralPath (Join-Path $packageDirectory 'START-HERE.txt') -Encoding utf8

Add-Type -AssemblyName System.IO.Compression.FileSystem
$zipPath = Join-Path $runDirectory ($packageName + '.zip')
[IO.Compression.ZipFile]::CreateFromDirectory($packageDirectory, $zipPath, [IO.Compression.CompressionLevel]::Optimal, $true)
$checksumPath = Join-Path $runDirectory 'SHA256SUMS.txt'
$checksum = (Get-FileHash -LiteralPath $zipPath -Algorithm SHA256).Hash.ToLowerInvariant()
$releaseAssets = @($zipPath)
$installerPackage = $null
if (!$PortableOnly) {
    $installerPackage = & (Join-Path $PSScriptRoot 'Pack-KlippInstaller.ps1') -PackageDirectory $packageDirectory -Version $Version -PreviousReleaseDirectory $PreviousReleaseDirectory
    $releaseAssets += @($installerPackage.ReleaseAssets)
}
$checksumLines = foreach ($releaseAsset in $releaseAssets) {
    (Get-FileHash -LiteralPath $releaseAsset -Algorithm SHA256).Hash.ToLowerInvariant() + '  ' + [IO.Path]::GetFileName($releaseAsset)
}
$checksumLines | Set-Content -LiteralPath $checksumPath -Encoding ascii
$releaseAssets += $checksumPath
[ordered]@{
    version = $Version
    sourceCommit = $sourceCommit
    packageDirectory = $packageDirectory
    installer = $installerPackage.Installer
    updateFeed = $installerPackage.UpdateFeed
    releaseAssets = @($releaseAssets | ForEach-Object {
        [ordered]@{
            path = $_
            name = [IO.Path]::GetFileName($_)
            size = (Get-Item -LiteralPath $_).Length
            sha256 = (Get-FileHash -LiteralPath $_ -Algorithm SHA256).Hash.ToLowerInvariant()
        }
    })
} | ConvertTo-Json -Depth 6 | Set-Content -LiteralPath (Join-Path $runDirectory 'release-artifacts.json') -Encoding utf8

[pscustomobject]@{
    Version = $Version
    PackageDirectory = $packageDirectory
    Archive = $zipPath
    Checksums = $checksumPath
    Sha256 = $checksum
    Installer = $installerPackage.Installer
    UpdateFeed = $installerPackage.UpdateFeed
    UpdatePackages = @($installerPackage.UpdatePackages)
    ReleaseAssets = $releaseAssets
}
