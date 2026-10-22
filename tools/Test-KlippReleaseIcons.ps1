[CmdletBinding()]
param(
    [Parameter(Mandatory = $true)][string]$PackageDirectory,
    [string]$InstallerPath = '',
    [string]$FullPackagePath = ''
)

$ErrorActionPreference = 'Stop'
$repoRoot = [IO.Path]::GetFullPath((Join-Path $PSScriptRoot '..'))
# Use native Windows icon loading/rendering through the .NET harness. The
# .NET Framework System.Drawing ICO decoder mishandles multi-frame PNG icons.
$arguments = @('run', '--project', (Join-Path $PSScriptRoot 'Klipp.ShortcutHarness/Klipp.ShortcutHarness.csproj'),
    '-c', 'Release', '--', '--verify-release-icons',
    '--reference', (Join-Path $repoRoot 'src/Klipp.Desktop/Assets/Klipp.ico'),
    '--package', ([IO.Path]::GetFullPath($PackageDirectory)))
if ($InstallerPath) { $arguments += @('--installer', ([IO.Path]::GetFullPath($InstallerPath))) }
if ($FullPackagePath) { $arguments += @('--full-package', ([IO.Path]::GetFullPath($FullPackagePath))) }
& dotnet @arguments | Out-Host
if ($LASTEXITCODE -ne 0) { throw "Release icon validation failed ($LASTEXITCODE)." }
