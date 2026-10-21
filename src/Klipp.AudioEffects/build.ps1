param(
    [ValidateSet('x64', 'ARM64')][string]$Architecture = 'x64',
    [string]$OutputDirectory = ''
)
$ErrorActionPreference = 'Stop'
$compilerLocator = "${env:ProgramFiles(x86)}/Microsoft Visual Studio/Installer/vswhere.exe"
$installation = & $compilerLocator -latest -prerelease -products '*' -requires Microsoft.VisualStudio.Component.VC.Tools.x86.x64 -property installationPath
if (!$installation) { throw 'Visual Studio C++ build tools are required to build Klipp.AudioEffects.' }
$vcvars = Join-Path $installation ('VC/Auxiliary/Build/' + $(if ($Architecture -eq 'ARM64') { 'vcvarsamd64_arm64.bat' } else { 'vcvars64.bat' }))
if (!(Test-Path -LiteralPath $vcvars)) { throw "Install the Visual Studio C++ $Architecture build tools to build Klipp.AudioEffects for $Architecture." }
if (!$OutputDirectory) { $OutputDirectory = Join-Path $PSScriptRoot "bin/$Architecture/Release" }
$output = [IO.Path]::GetFullPath($OutputDirectory)
New-Item -ItemType Directory -Path $output -Force | Out-Null
$buildCommand = 'call "' + $vcvars + '" >nul && cl /nologo /O2 /W4 /std:c++20 /EHsc /MD /LD /DWIN32_LEAN_AND_MEAN /DNOMINMAX "' + (Join-Path $PSScriptRoot 'AudioEffects.cpp') + '" /link windowsapp.lib /DEF:"' + (Join-Path $PSScriptRoot 'AudioEffects.def') + '" /OUT:Klipp.AudioEffects.dll'
$mutexName = 'Local\Klipp.AudioEffects.Build.' + [Convert]::ToBase64String([Security.Cryptography.SHA256]::Create().ComputeHash([Text.Encoding]::UTF8.GetBytes($output.ToUpperInvariant()))).Replace('/','_')
$buildMutex = New-Object Threading.Mutex($false, $mutexName)
$ownsMutex = $false
try {
    try { $ownsMutex = $buildMutex.WaitOne(60000) } catch [Threading.AbandonedMutexException] { $ownsMutex = $true }
    if (!$ownsMutex) { throw 'Another audio effects build is still running.' }
    Push-Location $output
    try { & $env:ComSpec /d /s /c $buildCommand; if ($LASTEXITCODE -ne 0) { throw "Audio effects build failed ($LASTEXITCODE)." } }
    finally { Pop-Location }
}
finally { if ($ownsMutex) { $buildMutex.ReleaseMutex() }; $buildMutex.Dispose() }
Write-Output (Join-Path $output 'Klipp.AudioEffects.dll')
