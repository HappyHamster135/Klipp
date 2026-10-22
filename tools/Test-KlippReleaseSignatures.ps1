[CmdletBinding()]
param(
    [Parameter(Mandatory = $true)]
    [string]$ReleaseManifestPath,
    [string]$ExpectedPublisher = ''
)

# Verification only: this script never signs, trusts certificates, runs payloads
# or changes release assets. A failed check must prevent release publication.
$ErrorActionPreference = 'Stop'
Add-Type -AssemblyName System.IO.Compression
Add-Type -AssemblyName System.IO.Compression.FileSystem
Add-Type -AssemblyName System.Security
$manifestPath = [IO.Path]::GetFullPath($ReleaseManifestPath)
$releaseRoot = [IO.Path]::GetDirectoryName($manifestPath)
$manifest = Get-Content -LiteralPath $manifestPath -Raw | ConvertFrom-Json
if ([string]$manifest.version -notmatch '^\d+\.\d+\.\d+(?:-[0-9A-Za-z.-]+)?$') { throw 'Invalid release version.' }
$version = [string]$manifest.version
$requiredBinaries = @('Klipp.Desktop.exe', 'Klipp.Desktop.dll', 'Klipp.Core.dll', 'Klipp.Capture.dll',
    'Klipp.Encoding.dll', 'Klipp.Storage.dll', 'Klipp.AudioEffects.dll', 'Klipp.GameHook.dll')

function Resolve-ReleaseAsset([string]$Path) {
    if ([string]::IsNullOrWhiteSpace($Path)) { throw 'Release asset path is missing.' }
    if (![IO.Path]::IsPathRooted($Path)) { $Path = Join-Path $releaseRoot $Path }
    $resolved = [IO.Path]::GetFullPath($Path)
    if (!$resolved.StartsWith($releaseRoot.TrimEnd('\') + '\', [StringComparison]::OrdinalIgnoreCase)) {
        throw 'Release asset paths must stay within the release directory.'
    }
    if (!(Test-Path -LiteralPath $resolved -PathType Leaf)) { throw "Missing release asset: $resolved" }
    return $resolved
}

$assetPaths = @{}
$assetNames = [Collections.Generic.HashSet[string]]::new([StringComparer]::OrdinalIgnoreCase)
foreach ($asset in @($manifest.releaseAssets)) {
    $name = [string]$asset.name
    if ([string]::IsNullOrWhiteSpace($name) -or [IO.Path]::GetFileName($name) -ne $name -or !$assetNames.Add($name)) {
        throw 'Release asset filenames must be unique basenames.'
    }
    $path = Resolve-ReleaseAsset ([string]$asset.path)
    if ([IO.Path]::GetFileName($path) -ne $name) { throw "Release asset filename mismatch: $name" }
    if ([string]$asset.sha256 -notmatch '^[0-9a-fA-F]{64}$' -or
        (Get-Item -LiteralPath $path).Length -ne $asset.size -or
        (Get-FileHash -LiteralPath $path -Algorithm SHA256).Hash -ne $asset.sha256) {
        throw "Release asset size or SHA-256 mismatch: $name"
    }
    $assetPaths[$name] = $path
}
$installer = Resolve-ReleaseAsset ([string]$manifest.installer)
$feedPath = Resolve-ReleaseAsset ([string]$manifest.updateFeed)
if ($assetPaths['Klipp-Setup.exe'] -ne $installer -or $assetPaths['releases.win.json'] -ne $feedPath) {
    throw 'Installer and update feed must be listed in the release manifest.'
}
$portableName = "Klipp-$version-win-x64.zip"
$packageName = "HappyHamster135.Klipp-$version-full.nupkg"
if (!$assetPaths.ContainsKey($portableName) -or !$assetPaths.ContainsKey($packageName)) {
    throw 'The release must include its current portable ZIP and full update package.'
}
$feed = Get-Content -LiteralPath $feedPath -Raw | ConvertFrom-Json
$full = @($feed.Assets | Where-Object {
    $_.PackageId -eq 'HappyHamster135.Klipp' -and $_.Version -eq $version -and $_.Type -eq 'Full'
})
if ($full.Count -ne 1 -or $full[0].FileName -ne $packageName) { throw 'Update feed has an invalid current full package.' }
$packagePath = $assetPaths[$packageName]
if ((Get-Item -LiteralPath $packagePath).Length -ne $full[0].Size -or
    [string]$full[0].SHA256 -notmatch '^[0-9a-fA-F]{64}$' -or
    (Get-FileHash -LiteralPath $packagePath -Algorithm SHA256).Hash -ne $full[0].SHA256) {
    throw 'Update package does not match its feed size or SHA-256.'
}
if ($full[0].SHA1 -and (Get-FileHash -LiteralPath $packagePath -Algorithm SHA1).Hash -ne $full[0].SHA1) {
    throw 'Update package does not match its feed SHA-1.'
}

$verificationDirectory = Join-Path ([IO.Path]::GetTempPath()) ('Klipp-Signature-Check-' + [Guid]::NewGuid().ToString('N'))
[void][IO.Directory]::CreateDirectory($verificationDirectory)
$toVerify = [Collections.Generic.List[object]]::new()
$toVerify.Add([pscustomobject]@{ Path = $installer; Label = 'installer/Klipp-Setup.exe' })

function Read-ReleaseArchive([string]$Path, [string]$Kind) {
    $zip = [IO.Compression.ZipFile]::OpenRead($Path)
    try {
        $entryNames = [Collections.Generic.HashSet[string]]::new([StringComparer]::OrdinalIgnoreCase)
        $binaryNames = [Collections.Generic.HashSet[string]]::new([StringComparer]::OrdinalIgnoreCase)
        $count = 0
        foreach ($entry in $zip.Entries) {
            $name = $entry.FullName.Replace('\', '/')
            $parts = $name.TrimEnd('/').Split('/')
            if (!$name -or $name.StartsWith('/') -or $name.Contains(':') -or
                @($parts | Where-Object { $_ -in @('', '.', '..') }).Count -or !$entryNames.Add($name)) {
                throw "Unsafe or duplicate ZIP entry in ${Kind}: $name"
            }
            if ($name.EndsWith('/')) { continue }
            $baseName = $parts[-1]
            if ($baseName -notmatch '^(Klipp.*\.(exe|dll)|Squirrel\.exe|Update\.exe)$') { continue }
            $prefix = $name.Substring(0, $name.Length - $baseName.Length)
            $allowedPrefix = if ($Kind -eq 'update') { $prefix -eq 'lib/app/' }
                else { $prefix -eq "Klipp-$version-win-x64/" -or $prefix -eq 'current/' -or $prefix -eq '' }
            if (!$allowedPrefix -or !$binaryNames.Add($baseName)) {
                throw "Unexpected path or duplicate binary basename in ${Kind}: $name"
            }
            if ($baseName.IndexOfAny([IO.Path]::GetInvalidFileNameChars()) -ge 0 -or
                $entry.Length -lt 2 -or $entry.Length -gt 536870912) {
                throw "Invalid binary entry in ${Kind}: $name"
            }
            # Flatten only selected PE files into a fresh verification directory.
            # Untrusted archive paths are never used as extraction destinations.
            $destination = Join-Path $verificationDirectory ("$Kind-$count-$baseName")
            $inputStream = $entry.Open()
            $outputStream = [IO.File]::Open($destination, [IO.FileMode]::CreateNew)
            try { $inputStream.CopyTo($outputStream) }
            finally { $outputStream.Dispose(); $inputStream.Dispose() }
            $toVerify.Add([pscustomobject]@{ Path = $destination; Label = "$Kind/$name" })
            $count++
        }
        foreach ($required in $requiredBinaries) {
            if (!$binaryNames.Contains($required)) { throw "Missing required binary in ${Kind}: $required" }
        }
        if ($Kind -eq 'update' -and (!$binaryNames.Contains('Klipp_ExecutionStub.exe') -or
            !($binaryNames.Contains('Squirrel.exe') -or $binaryNames.Contains('Update.exe')))) {
            throw 'The update package must include its signed execution stub and updater.'
        }
        if ($Kind -eq 'portable' -and $entryNames.Contains('current/Klipp.Desktop.exe') -and
            !($entryNames.Contains('Klipp.exe') -and $entryNames.Contains('Update.exe'))) {
            throw 'The portable package must include its signed execution stub and updater.'
        }
    }
    finally { $zip.Dispose() }
}

Read-ReleaseArchive $assetPaths[$portableName] 'portable'
Read-ReleaseArchive $packagePath 'update'

function Test-Rfc3161Timestamp([string]$Path) {
    # Authenticode's TimeStamperCertificate alone also accepts legacy timestamps.
    # Confirm the RFC 3161 unsigned attribute in the PE certificate table.
    $stream = [IO.File]::OpenRead($Path)
    $reader = [IO.BinaryReader]::new($stream)
    try {
        if ($stream.Length -lt 64) { return $false }
        $stream.Position = 60
        $peOffset = [long]$reader.ReadUInt32()
        if ($peOffset -gt $stream.Length - 152) { return $false }
        $stream.Position = $peOffset
        if ($reader.ReadUInt32() -ne 0x4550) { return $false }
        $optionalOffset = $peOffset + 24
        $stream.Position = $optionalOffset
        $magic = $reader.ReadUInt16()
        $directoryOffset = if ($magic -eq 0x10b) { 96 } elseif ($magic -eq 0x20b) { 112 } else { return $false }
        $stream.Position = $optionalOffset + $directoryOffset + 32
        $certificateOffset = [long]$reader.ReadUInt32()
        $certificateSize = [long]$reader.ReadUInt32()
        $end = $certificateOffset + $certificateSize
        if ($certificateOffset -eq 0 -or $certificateSize -lt 8 -or $end -gt $stream.Length) { return $false }
        $position = $certificateOffset
        while ($position -le $end - 8) {
            $stream.Position = $position
            $length = [long]$reader.ReadUInt32()
            [void]$reader.ReadUInt16()
            $type = $reader.ReadUInt16()
            if ($length -lt 8 -or $position + $length -gt $end -or $length -gt 16777216) { return $false }
            if ($type -eq 2) {
                $cms = [Security.Cryptography.Pkcs.SignedCms]::new()
                $cms.Decode($reader.ReadBytes([int]($length - 8)))
                foreach ($signer in $cms.SignerInfos) {
                    foreach ($attribute in $signer.UnsignedAttributes) {
                        if ($attribute.Oid.Value -eq '1.3.6.1.4.1.311.3.3.1') { return $true }
                    }
                }
            }
            $position += [long]([Math]::Ceiling($length / 8.0) * 8)
        }
        return $false
    }
    finally { $reader.Dispose(); $stream.Dispose() }
}

$verified = [Collections.Generic.List[object]]::new()
foreach ($binary in $toVerify) {
    $signature = Get-AuthenticodeSignature -LiteralPath $binary.Path
    $certificate = $signature.SignerCertificate
    if ($signature.Status -ne 'Valid' -or !$certificate) {
        throw "Signature verification failed ($($signature.Status)): $($binary.Label)"
    }
    if ($certificate.Subject -eq $certificate.Issuer -or $certificate.Subject -match 'Klipp Local Dev') {
        throw "A development or self-signed certificate cannot sign a public release: $($binary.Label)"
    }
    $codeSigning = @($certificate.EnhancedKeyUsageList | Where-Object ObjectId -EQ '1.3.6.1.5.5.7.3.3').Count -gt 0
    if (!$codeSigning) { throw "Code-signing certificate usage is missing: $($binary.Label)" }
    if ($ExpectedPublisher -and $certificate.Subject -ne $ExpectedPublisher) {
        throw "Unexpected signing publisher: $($binary.Label)"
    }
    if (!$signature.TimeStamperCertificate -or !(Test-Rfc3161Timestamp $binary.Path)) {
        throw "A valid RFC 3161 timestamp is required: $($binary.Label)"
    }
    $verified.Add([pscustomobject]@{ File = $binary.Label; Publisher = $certificate.Subject; Thumbprint = $certificate.Thumbprint })
}
[pscustomobject]@{
    Version = $version
    VerifiedBinaries = $verified.Count
    Signatures = @($verified)
    VerificationDirectory = $verificationDirectory
}
