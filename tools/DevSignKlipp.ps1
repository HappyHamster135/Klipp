param(
    [string]$OutputDirectory,
    [string[]]$AdditionalDirectories = @()
)

$ErrorActionPreference = "Stop"

$subject = "CN=Klipp Local Dev"
$repoRoot = Resolve-Path (Join-Path $PSScriptRoot "..")
if ($null -eq $OutputDirectory) {
    $OutputDirectory = string.Empty
}
$OutputDirectory = $OutputDirectory.Trim().Trim('"')

function Find-SignTool {
    $kitsRoot = Join-Path ${env:ProgramFiles(x86)} "Windows Kits\10\bin"
    if (-not (Test-Path $kitsRoot)) {
        return $null
    }

    Get-ChildItem -Path $kitsRoot -Recurse -Filter signtool.exe -ErrorAction SilentlyContinue |
        Where-Object { $_.FullName -match "\\x64\\signtool\.exe$" } |
        Sort-Object FullName -Descending |
        Select-Object -First 1 -ExpandProperty FullName
}

function Get-KlippCertificate {
    Get-ChildItem Cert:\CurrentUser\My |
        Where-Object {
            $_.Subject -eq $subject -and
            $_.HasPrivateKey -and
            $_.NotAfter -gt (Get-Date).AddDays(30)
        } |
        Sort-Object NotAfter -Descending |
        Select-Object -First 1
}

$cert = Get-KlippCertificate
if (-not $cert) {
    Write-Host "[Klipp] Dev signing skipped: no '$subject' certificate in CurrentUser\My."
    exit 0
}

$signTool = Find-SignTool
if (-not $signTool) {
    Write-Host "[Klipp] Dev signing skipped: signtool.exe was not found."
    exit 0
}

$roots = @()
if ($OutputDirectory) {
    if (Test-Path -LiteralPath $OutputDirectory) {
        $roots += (Resolve-Path -LiteralPath $OutputDirectory).Path
        $appx = Join-Path $OutputDirectory "AppX"
        if (Test-Path -LiteralPath $appx) {
            $roots += (Resolve-Path -LiteralPath $appx).Path
        }
    }
}
else {
    $desktopBin = Join-Path $repoRoot "src\Klipp.Desktop\bin"
    if (Test-Path $desktopBin) {
        $roots += (Resolve-Path $desktopBin).Path
    }
}

foreach ($directory in $AdditionalDirectories) {
    if (-not [string]::IsNullOrWhiteSpace($directory)) {
        $directory = $directory.Trim().Trim('"')
        if (Test-Path -LiteralPath $directory) {
            $roots += (Resolve-Path -LiteralPath $directory).Path
        }
    }
}

if ($roots.Count -eq 0) {
    Write-Host "[Klipp] Dev signing skipped: no output directories found."
    exit 0
}

$files = foreach ($root in $roots) {
    Get-ChildItem -Path $root -Recurse -File -Include "Klipp*.exe", "Klipp*.dll", "apphost.exe" -ErrorAction SilentlyContinue
}

$uniqueFiles = $files | Sort-Object FullName -Unique
foreach ($file in $uniqueFiles) {
    $signature = Get-AuthenticodeSignature -FilePath $file.FullName
    if ($signature.Status -eq "Valid" -and
        $signature.SignerCertificate -and
        $signature.SignerCertificate.Thumbprint -eq $cert.Thumbprint) {
        continue
    }

    & $signTool sign /fd SHA256 /sha1 $cert.Thumbprint $file.FullName | Out-Host
    if ($LASTEXITCODE -ne 0) {
        throw "signtool failed for $($file.FullName)"
    }
}

Write-Host "[Klipp] Dev-signed $($uniqueFiles.Count) app binaries with $($cert.Thumbprint)."
