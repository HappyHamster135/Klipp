$ErrorActionPreference = 'Stop'
$creator = Join-Path $PSScriptRoot 'New-KlippCommunityConfiguration.ps1'
$verifier = Join-Path $PSScriptRoot 'Test-KlippCommunityConfiguration.ps1'
$testRoot = [IO.Path]::GetFullPath((Join-Path ([IO.Path]::GetTempPath()) ('klipp-public-config-' + [Guid]::NewGuid().ToString('N'))))
[IO.Directory]::CreateDirectory($testRoot) | Out-Null
$checks = 0
function Check([bool]$Condition, [string]$Label) { if (!$Condition) { throw "Public configuration check failed: $Label" }; $script:checks++ }
function Reject([scriptblock]$Action, [string]$Label) { $rejected = $false; try { & $Action | Out-Null } catch { $rejected = $true }; Check $rejected $Label }
function FixturePackage([string]$Name, [hashtable]$Files) {
    $path = Join-Path $testRoot ($Name + '.nupkg')
    $archive = [IO.Compression.ZipFile]::Open($path, [IO.Compression.ZipArchiveMode]::Create)
    try {
        foreach ($entryPath in $Files.Keys) {
            $stream = $archive.CreateEntry($entryPath).Open()
            try { $bytes = [Text.UTF8Encoding]::new($false).GetBytes($Files[$entryPath]); $stream.Write($bytes, 0, $bytes.Length) }
            finally { $stream.Dispose() }
        }
    }
    finally { $archive.Dispose() }
    return $path
}
Add-Type -AssemblyName System.IO.Compression
Add-Type -AssemblyName System.IO.Compression.FileSystem
try {
    $publicKey = 'sb_publishable_publicfixture123456789'
    $url = 'https://fixture-project.supabase.co'
    $empty = Join-Path $testRoot 'capture-only.json'
    $result = & $creator -OutputPath $empty -ProjectUrl '' -PublishableKey '' -MediaApiUrl ''
    Check (!$result.Configured -and !(Test-Path -LiteralPath $empty)) 'capture-only creates no configuration'
    Reject { & $creator -OutputPath $empty -ProjectUrl '' -PublishableKey '' -MediaApiUrl '' -RequireOnlineConfiguration } 'official candidates cannot silently lose account configuration'
    foreach ($bad in @(
        @{ ProjectUrl = $url; PublishableKey = ''; MediaApiUrl = '' },
        @{ ProjectUrl = ''; PublishableKey = $publicKey; MediaApiUrl = '' },
        @{ ProjectUrl = ''; PublishableKey = ''; MediaApiUrl = 'https://media.example.com' },
        @{ ProjectUrl = $url; PublishableKey = 'sb_secret_private'; MediaApiUrl = '' },
        @{ ProjectUrl = $url; PublishableKey = 'eyJpdiI.eyJyb2xlIjoic2VydmljZV9yb2xlIn0.signature'; MediaApiUrl = '' },
        @{ ProjectUrl = 'http://fixture-project.supabase.co'; PublishableKey = $publicKey; MediaApiUrl = '' },
        @{ ProjectUrl = 'https://user@fixture-project.supabase.co'; PublishableKey = $publicKey; MediaApiUrl = '' },
        @{ ProjectUrl = $url; PublishableKey = $publicKey; MediaApiUrl = 'http://media.example.com' },
        @{ ProjectUrl = $url; PublishableKey = $publicKey; MediaApiUrl = 'https://user@media.example.com' },
        @{ ProjectUrl = $url; PublishableKey = $publicKey; MediaApiUrl = 'https://media.example.com/api' },
        @{ ProjectUrl = $url; PublishableKey = $publicKey; MediaApiUrl = 'https://media.example.com/?token=private' }
    )) { Reject { & $creator -OutputPath (Join-Path $testRoot 'invalid.json') @bad } 'reject incomplete, privileged or untrusted public configuration' }
    $legacy = Join-Path $testRoot 'supabase-only.json'
    $result = & $creator -OutputPath $legacy -ProjectUrl $url -PublishableKey $publicKey -MediaApiUrl ''
    $fields = Get-Content -Raw -LiteralPath $legacy | ConvertFrom-Json
    Check ($result.Configured -and !$result.HomeMediaConfigured -and @($fields.PSObject.Properties).Count -eq 2) 'optional home host omitted'
    $homeConfigPath = Join-Path $testRoot 'home.json'
    $result = & $creator -OutputPath $homeConfigPath -ProjectUrl $url -PublishableKey $publicKey -MediaApiUrl 'https://media.example.com/'
    $fields = Get-Content -Raw -LiteralPath $homeConfigPath | ConvertFrom-Json
    Check ($result.HomeMediaConfigured -and @($fields.PSObject.Properties).Count -eq 3 -and $fields.mediaApiUrl -ceq 'https://media.example.com') 'home origin normalized without extra fields'
    Reject { & $creator -OutputPath $homeConfigPath -ProjectUrl $url -PublishableKey $publicKey } 'never overwrite an existing publisher configuration'
    $source = [IO.File]::ReadAllText($homeConfigPath)
    $correct = FixturePackage 'correct' @{ 'lib/app/community.json' = $source }
    Check ((& $verifier -ExpectedConfigurationPath $homeConfigPath -FullPackagePath $correct).UpdatePayloadVerified) 'configured update payload matches'
    $none = FixturePackage 'capture-only' @{ 'lib/app/fixture.txt' = 'no config' }
    Check ((& $verifier -ExpectedConfigurationPath $empty -FullPackagePath $none).UpdatePayloadVerified) 'capture-only package matches'
    Reject { & $verifier -ExpectedConfigurationPath $homeConfigPath -FullPackagePath $none } 'missing configuration rejected'
    Reject { & $verifier -ExpectedConfigurationPath $empty -FullPackagePath $correct } 'unexpected configuration rejected'
    $changed = FixturePackage 'changed' @{ 'lib/app/community.json' = '{}' }
    Reject { & $verifier -ExpectedConfigurationPath $homeConfigPath -FullPackagePath $changed } 'changed configuration rejected'
    $duplicate = FixturePackage 'duplicate' @{ 'lib/app/community.json' = $source; 'other/community.json' = $source }
    Reject { & $verifier -ExpectedConfigurationPath $homeConfigPath -FullPackagePath $duplicate } 'duplicate configuration rejected'
    $misplaced = FixturePackage 'misplaced' @{ 'other/community.json' = $source }
    Reject { & $verifier -ExpectedConfigurationPath $homeConfigPath -FullPackagePath $misplaced } 'misplaced configuration rejected'
    Write-Host "PASS $checks isolated public configuration and update payload checks"
}
finally {
    # Remove only files directly inside the newly-created verified fixture folder.
    # No application data, installed files or source configuration is touched.
    $tempParent = [IO.Path]::GetFullPath([IO.Path]::GetTempPath()).TrimEnd([IO.Path]::DirectorySeparatorChar) + [IO.Path]::DirectorySeparatorChar
    if (!$testRoot.StartsWith($tempParent, [StringComparison]::OrdinalIgnoreCase)) { throw 'Unsafe fixture cleanup path.' }
    foreach ($file in Get-ChildItem -LiteralPath $testRoot -File) { Remove-Item -LiteralPath $file.FullName -Force }
    Remove-Item -LiteralPath $testRoot
}
