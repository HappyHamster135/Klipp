[CmdletBinding()]
param(
    [Parameter(Mandatory = $true)]
    [ValidatePattern('^\d+\.\d+\.\d+(?:-[0-9A-Za-z.-]+)?$')]
    [string]$Version,
    [Parameter(Mandatory = $true)]
    [string]$OutputPath,
    [string]$ChangelogPath = (Join-Path $PSScriptRoot '../CHANGELOG.md'),
    [string]$SourcePath = ''
)

$ErrorActionPreference = 'Stop'
$OutputPath = [IO.Path]::GetFullPath($OutputPath)
$inputPath = [IO.Path]::GetFullPath($(if ($SourcePath) { $SourcePath } else { $ChangelogPath }))
if ($OutputPath.Equals($inputPath, [StringComparison]::OrdinalIgnoreCase)) { throw 'Release notes output must not overwrite its source.' }
if (!(Test-Path -LiteralPath $inputPath -PathType Leaf)) { throw 'Release notes source was not found.' }
if ((Get-Item -LiteralPath $inputPath).Length -gt 1048576) { throw 'Release notes source is too large.' }

if ($SourcePath) {
    $markdown = [IO.File]::ReadAllText($inputPath).Trim()
}
else {
    $lines = [IO.File]::ReadAllLines($inputPath)
    $headings = @()
    $fenceMarker = ''
    $fenceLength = 0
    for ($index = 0; $index -lt $lines.Length; $index++) {
        if ($fenceMarker) {
            if ($lines[$index] -match '^ {0,3}(`{3,}|~{3,})[ \t]*$' -and
                [string]$Matches[1][0] -ceq $fenceMarker -and $Matches[1].Length -ge $fenceLength) { $fenceMarker = '' }
            continue
        }
        if ($lines[$index] -match '^ {0,3}(`{3,}|~{3,})') {
            $fenceMarker = [string]$Matches[1][0]
            $fenceLength = $Matches[1].Length
            continue
        }
        if ($lines[$index] -match '^ {0,3}##[ \t]+([^ \t]+)(?:[ \t]|$)') {
            $headings += [pscustomobject]@{ Index = $index; Version = $Matches[1] }
        }
    }
    # Custom QA/developer versions may not have a changelog entry. An empty
    # result is intentional: never silently attach another version's notes.
    $starts = @($headings | Where-Object { $_.Version -ceq $Version })
    if ($starts.Count -eq 0) { return }
    if ($starts.Count -ne 1) { throw "Multiple changelog sections match version $Version." }
    $start = $starts[0].Index + 1
    $end = $lines.Length
    $next = $headings | Where-Object { $_.Index -ge $start } | Select-Object -First 1
    if ($next) { $end = $next.Index }
    $markdown = if ($end -gt $start) { ($lines[$start..($end - 1)] -join "`n").Trim() } else { '' }
}
if ([string]::IsNullOrWhiteSpace($markdown)) { throw "Release notes for $Version are empty." }
$markdown = $markdown.Replace("`r`n", "`n").Replace("`r", "`n")
if ([Text.Encoding]::UTF8.GetByteCount($markdown) -gt 131072) { throw 'Version-specific release notes are too large.' }
if (Test-Path -LiteralPath $OutputPath) { throw 'Release notes output must be a new file.' }
$outputParent = [IO.Path]::GetDirectoryName($OutputPath)
if (!(Test-Path -LiteralPath $outputParent -PathType Container)) { New-Item -ItemType Directory -Path $outputParent | Out-Null }
[IO.File]::WriteAllText($OutputPath, $markdown + "`n", [Text.UTF8Encoding]::new($false))
[pscustomobject]@{ Version = $Version; Path = $OutputPath; Markdown = $markdown }
