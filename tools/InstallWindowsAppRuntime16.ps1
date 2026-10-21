$ErrorActionPreference = "Stop"

$url = "https://aka.ms/windowsappsdk/1.6/latest/windowsappruntimeinstall-x64.exe"
$out = Join-Path $env:TEMP "windowsappruntimeinstall-1.6-x64.exe"

Write-Host "[Klipp] Downloading Windows App Runtime 1.6 x64..."
Invoke-WebRequest -Uri $url -OutFile $out -UseBasicParsing

Write-Host "[Klipp] Installing Windows App Runtime 1.6 x64..."
Start-Process -FilePath $out -ArgumentList "--quiet" -Wait

Write-Host "[Klipp] Installed Windows App Runtime packages:"
Get-AppxPackage -Name MicrosoftCorporationII.WinAppRuntime* |
    Select-Object Name,Version,PackageFullName |
    Format-Table -AutoSize
