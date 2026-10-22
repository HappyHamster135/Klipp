param(
    [string]$OutputDirectory = "$PSScriptRoot/bin/x64/Release",
    [ValidatePattern('^\d+\.\d+\.\d+(?:-[0-9A-Za-z.-]+)?$')]
    [string]$Version = '1.0.8'
)
$ErrorActionPreference = 'Stop'
. (Join-Path $PSScriptRoot '../Klipp.NativeVersion.ps1')
$compilerLocator = "${env:ProgramFiles(x86)}/Microsoft Visual Studio/Installer/vswhere.exe"
$installation = & $compilerLocator -latest -prerelease -products '*' -requires Microsoft.VisualStudio.Component.VC.Tools.x86.x64 -property installationPath
if (!$installation) { throw 'The Visual Studio x64 C++ build tools are required to build Klipp.GameHook.' }
$vcvars = Join-Path $installation 'VC/Auxiliary/Build/vcvars64.bat'
$output = [IO.Path]::GetFullPath($OutputDirectory)
New-Item -ItemType Directory -Path $output -Force | Out-Null
$minhook = Join-Path $PSScriptRoot 'third_party/minhook'
$sources = @('src/buffer.c','src/hook.c','src/trampoline.c','src/hde/hde64.c') | ForEach-Object { '"' + (Join-Path $minhook $_) + '"' }
# Keep this command in one shell. Inputs are explicit paths, never user media paths.
$buildCommand = 'call "' + $vcvars + '" >nul && rc /nologo /I "' + $output + '" /fo Klipp.Version.res "' + (Join-Path $PSScriptRoot '../Klipp.VersionInfo.rc') + '" && cl /nologo /O2 /W4 /MD /DWIN32_LEAN_AND_MEAN /D_WIN32_WINNT=0x0A00 /DNOMINMAX /c ' + ($sources -join ' ') + ' && cl /nologo /O2 /W4 /std:c++20 /EHsc /MD /c /FoGameHook.obj /DWIN32_LEAN_AND_MEAN /D_WIN32_WINNT=0x0A00 /DNOMINMAX /I"' + (Join-Path $minhook 'include') + '" "' + (Join-Path $PSScriptRoot 'GameHook.cpp') + '" && link /nologo /DLL /MACHINE:X64 GameHook.obj buffer.obj hook.obj trampoline.obj hde64.obj Klipp.Version.res d3d11.lib dxgi.lib d3dcompiler.lib user32.lib advapi32.lib /OUT:Klipp.GameHook.dll'
$mutexName = 'Local\Klipp.GameHook.Build.' + [Convert]::ToBase64String([Security.Cryptography.SHA256]::Create().ComputeHash([Text.Encoding]::UTF8.GetBytes($output.ToUpperInvariant()))).Replace('/','_')
$buildMutex = New-Object Threading.Mutex($false, $mutexName)
$ownsMutex = $false
try {
    try { $ownsMutex = $buildMutex.WaitOne(60000) } catch [Threading.AbandonedMutexException] { $ownsMutex = $true }
    if (!$ownsMutex) { throw 'Another Klipp native build is still running.' }
    Write-KlippNativeVersionHeader -Version $Version -FileName 'Klipp.GameHook.dll' -OutputDirectory $output
    Push-Location $output
    try { & $env:ComSpec /d /s /c $buildCommand; if ($LASTEXITCODE -ne 0) { throw "Native build failed ($LASTEXITCODE)." } }
    finally { Pop-Location }
    Copy-Item -LiteralPath (Join-Path $minhook 'LICENSE.txt') -Destination (Join-Path $output 'MinHook.LICENSE.txt') -Force
    [IO.File]::WriteAllText((Join-Path $output 'Klipp.NativeVersion.txt'), $Version + [Environment]::NewLine, [Text.Encoding]::ASCII)
}
finally { if ($ownsMutex) { $buildMutex.ReleaseMutex() }; $buildMutex.Dispose() }
Write-Output (Join-Path $output 'Klipp.GameHook.dll')
