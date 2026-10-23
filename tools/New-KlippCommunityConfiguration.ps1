[CmdletBinding()]
param(
    [Parameter(Mandatory = $true)][string]$OutputPath,
    [string]$ProjectUrl = $env:KLIPP_SUPABASE_URL,
    [string]$PublishableKey = $env:KLIPP_SUPABASE_PUBLISHABLE_KEY,
    [string]$MediaApiUrl = $env:KLIPP_MEDIA_API_URL,
    [switch]$RequireOnlineConfiguration
)

$ErrorActionPreference = 'Stop'
$ProjectUrl = ([string]$ProjectUrl).Trim()
$PublishableKey = ([string]$PublishableKey).Trim()
$MediaApiUrl = ([string]$MediaApiUrl).Trim()
$OutputPath = [IO.Path]::GetFullPath($OutputPath)
if (Test-Path -LiteralPath $OutputPath) { throw 'Publisher configuration output must be a new file.' }
if (!$ProjectUrl -and !$PublishableKey -and !$MediaApiUrl) {
    if ($RequireOnlineConfiguration) { throw 'Official Windows candidates require the public Supabase repository variables; recording and editing remain usable without signing in.' }
    # Optional Media must not prevent a capture-only build. Never reuse a stale
    # configuration from a different build when no publisher settings exist.
    return [pscustomobject]@{ Configured = $false; HomeMediaConfigured = $false }
}
if ($ProjectUrl -cnotmatch '^https://[a-z0-9-]+\.supabase\.co$' -or
    $PublishableKey.Length -gt 4096 -or $PublishableKey -cnotmatch '^sb_publishable_[A-Za-z0-9_-]{11,}$') {
    throw 'Set both KLIPP_SUPABASE_URL and KLIPP_SUPABASE_PUBLISHABLE_KEY to the public project values. Secret and service-role keys are not accepted.'
}
$configuration = [ordered]@{ projectUrl = $ProjectUrl; publishableKey = $PublishableKey }
if ($MediaApiUrl) {
    $mediaUri = $null
    if ($MediaApiUrl.Length -gt 2048 -or ![Uri]::TryCreate($MediaApiUrl, [UriKind]::Absolute, [ref]$mediaUri) -or
        $mediaUri.Scheme -cne 'https' -or $mediaUri.AbsolutePath -cne '/' -or $mediaUri.UserInfo -or
        $mediaUri.Query -or $mediaUri.Fragment -or $mediaUri.Host.EndsWith('.invalid', [StringComparison]::OrdinalIgnoreCase)) {
        throw 'KLIPP_MEDIA_API_URL must be the reviewed HTTPS media origin, without a path, credentials or query.'
    }
    $configuration.mediaApiUrl = $mediaUri.GetLeftPart([UriPartial]::Authority)
}
[IO.Directory]::CreateDirectory([IO.Path]::GetDirectoryName($OutputPath)) | Out-Null
$bytes = [Text.UTF8Encoding]::new($false).GetBytes(($configuration | ConvertTo-Json) + "`n")
$stream = [IO.File]::Open($OutputPath, [IO.FileMode]::CreateNew, [IO.FileAccess]::Write, [IO.FileShare]::None)
try { $stream.Write($bytes, 0, $bytes.Length) }
finally { $stream.Dispose() }
[pscustomobject]@{ Configured = $true; HomeMediaConfigured = [bool]$MediaApiUrl }
