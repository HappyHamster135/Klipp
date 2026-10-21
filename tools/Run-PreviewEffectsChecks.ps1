param([switch]$VideoRangeChecks, [switch]$NotificationChecks, [switch]$FullscreenNotificationChecks, [switch]$CaptureSourceTargets)

$ErrorActionPreference = 'Stop'

# Win2D needs the installed Windows App SDK. Some dotnet SDK versions resolve
# Appx tasks under their own SDK folder instead of Visual Studio's installation.
# Pass the discovered task location for this build only.
$taskFile = $null
$vswhere = Join-Path ${env:ProgramFiles(x86)} 'Microsoft Visual Studio\Installer\vswhere.exe'
if (Test-Path -LiteralPath $vswhere) {
    $taskFile = & $vswhere -latest -prerelease -products '*' -find 'MSBuild\Microsoft\VisualStudio\*\AppxPackage\Microsoft.Build.Packaging.Pri.Tasks.dll' |
        Select-Object -First 1
}
$arguments = @('run', '--project', (Join-Path $PSScriptRoot 'Klipp.PreviewEffectsHarness'), '-p:Platform=x64')
if ($taskFile) {
    $taskDirectory = [IO.Path]::GetDirectoryName($taskFile) + '/'
    $arguments += "-p:AppxMSBuildToolsPath=$taskDirectory"
}
if ($CaptureSourceTargets) { $arguments += @('--', '--capture-source-targets') }
# Interactive QA: activate the fake fullscreen window and click the reported point.
elseif ($FullscreenNotificationChecks) { $arguments += @('--', '--fullscreen-notification-checks') }
elseif ($NotificationChecks) { $arguments += @('--', '--notification-checks') }
elseif ($VideoRangeChecks) { $arguments += @('--', '--video-range-checks') }
& dotnet @arguments
if ($LASTEXITCODE -ne 0) { throw "Preview FX pixel checks failed with exit code $LASTEXITCODE." }
