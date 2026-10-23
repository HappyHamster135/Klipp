[CmdletBinding()]
param(
    [Parameter(Mandatory = $true)][string]$ExpectedConfigurationPath,
    [Parameter(Mandatory = $true)][string]$FullPackagePath
)

$ErrorActionPreference = 'Stop'
Add-Type -AssemblyName System.IO.Compression.FileSystem
$configured = Test-Path -LiteralPath $ExpectedConfigurationPath -PathType Leaf
if ($configured -and (Get-Item -LiteralPath $ExpectedConfigurationPath).Length -gt 16384) {
    throw 'The public publisher configuration exceeds the application size limit.'
}
$archive = [IO.Compression.ZipFile]::OpenRead([IO.Path]::GetFullPath($FullPackagePath))
try {
    $entries = @($archive.Entries | Where-Object { $_.FullName -match '(?i)(?:^|/)community\.json$' })
    if (!$configured) {
        if ($entries.Count) { throw 'The capture-only update package unexpectedly contains publisher configuration.' }
    }
    else {
        if ($entries.Count -ne 1 -or $entries[0].Length -gt 16384) {
            throw 'The update package must contain exactly one public community.json beside the application.'
        }
        if ($entries[0].FullName -cne 'lib/app/community.json') {
            throw 'The public configuration is not beside the application in the update payload.'
        }
        $inputStream = $entries[0].Open()
        $hash = [Security.Cryptography.SHA256]::Create()
        try { $actual = [BitConverter]::ToString($hash.ComputeHash($inputStream)).Replace('-', '') }
        finally { $hash.Dispose(); $inputStream.Dispose() }
        if ($actual -cne (Get-FileHash -LiteralPath $ExpectedConfigurationPath -Algorithm SHA256).Hash) {
            throw 'The update package publisher configuration does not match the reviewed build configuration.'
        }
    }
}
finally { $archive.Dispose() }
[pscustomobject]@{ Configured = $configured; UpdatePayloadVerified = $true }
