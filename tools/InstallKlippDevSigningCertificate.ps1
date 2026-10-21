$ErrorActionPreference = "Stop"

$subject = "CN=Klipp Local Dev"

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

function Trust-KlippCertificate($cert) {
    foreach ($storeName in @("Root", "TrustedPublisher")) {
        $store = [System.Security.Cryptography.X509Certificates.X509Store]::new(
            $storeName,
            [System.Security.Cryptography.X509Certificates.StoreLocation]::CurrentUser)
        $store.Open([System.Security.Cryptography.X509Certificates.OpenFlags]::ReadWrite)
        try {
            $alreadyTrusted = $store.Certificates |
                Where-Object { $_.Thumbprint -eq $cert.Thumbprint } |
                Select-Object -First 1

            if (-not $alreadyTrusted) {
                $store.Add($cert)
            }
        }
        finally {
            $store.Close()
        }
    }
}

$cert = Get-KlippCertificate
if (-not $cert) {
    $cert = New-SelfSignedCertificate `
        -Type CodeSigningCert `
        -Subject $subject `
        -FriendlyName "Klipp Local Dev Code Signing" `
        -CertStoreLocation Cert:\CurrentUser\My `
        -KeyUsage DigitalSignature `
        -KeyExportPolicy NonExportable `
        -HashAlgorithm SHA256 `
        -KeyLength 2048 `
        -NotAfter (Get-Date).AddYears(3)
}

Trust-KlippCertificate $cert

Write-Host "[Klipp] Installed/trusted dev signing certificate:"
$cert | Select-Object Subject,Thumbprint,NotAfter | Format-List
