[CmdletBinding()]
param(
    [Parameter(Mandatory = $true)][string]$FilesJson,
    [Parameter(Mandatory = $true)][string]$ExpectedPublisher
)
$ErrorActionPreference = 'Stop'
$files = @([IO.File]::ReadAllText($FilesJson) | ConvertFrom-Json)
if (!$files.Count -or [string]::IsNullOrWhiteSpace($ExpectedPublisher)) { throw 'Signing verification requires files and an approved publisher.' }
foreach ($file in $files) {
    if (!(Test-Path -LiteralPath $file -PathType Leaf)) { throw 'The signed artifact is missing a requested file.' }
    $signature = Get-AuthenticodeSignature -LiteralPath $file
    $certificate = $signature.SignerCertificate
    if ($signature.Status -ne 'Valid' -or !$certificate -or !$signature.TimeStamperCertificate) {
        throw 'A returned file lacks a valid timestamped Authenticode signature.'
    }
    if ($certificate.Subject -cne $ExpectedPublisher -or $certificate.Subject -eq $certificate.Issuer -or
        $certificate.Subject -match 'Klipp Local Dev') { throw 'The returned publisher does not match the approved signing identity.' }
    $eku = @($certificate.Extensions | Where-Object { $_.Oid.Value -eq '2.5.29.37' } |
        ForEach-Object { $_.EnhancedKeyUsages } | ForEach-Object { $_.Value })
    if ($eku -notcontains '1.3.6.1.5.5.7.3.3') { throw 'The signing certificate does not permit code signing.' }
}
Write-Host "Verified $($files.Count) signed binaries."
