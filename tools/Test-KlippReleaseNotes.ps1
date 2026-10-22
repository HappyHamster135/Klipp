[CmdletBinding()]
param()

$ErrorActionPreference = 'Stop'
$fixture = Join-Path ([IO.Path]::GetTempPath()) ('Klipp-Release-Notes-QA-' + [Guid]::NewGuid().ToString('N'))
New-Item -ItemType Directory -Path $fixture | Out-Null
$script = Join-Path $PSScriptRoot 'New-KlippReleaseNotes.ps1'
$checks = 0
$arrow = [string][char]0x2192
$umlaut = [string][char]0xE4
function Check([bool]$Condition, [string]$Message) {
    if (!$Condition) { throw $Message }
    $script:checks++
}
function ExpectFailure([scriptblock]$Action, [string]$Message) {
    $failed = $false
    try { & $Action | Out-Null } catch { $failed = $true }
    Check $failed $Message
}
$changelog = Join-Path $fixture 'CHANGELOG.md'
@'
# Changelog

## 1.0.100 - Future
- Do not borrow a future version's notes.

## 1.0.10 - Current

- See what's new before restarting.
- Open the release notes <ARROW> details.
### More detail
Keep Markdown links such as [notes](https://example.test).
```text
## 1.0.10 - This is a code sample, not a duplicate version
## 1.0.1 - Also a code sample
```
- Preserve this last current-version bullet.

## 1.0.1 - Older
- Do not borrow an older version's notes.
'@.Replace('<ARROW>', $arrow) | Set-Content -LiteralPath $changelog -Encoding utf8
$output = Join-Path $fixture 'Current.md'
$result = & $script -Version '1.0.10' -ChangelogPath $changelog -OutputPath $output
Check ($result.Version -ceq '1.0.10' -and $result.Path -eq $output) 'Extraction lost its requested version/path.'
Check ($result.Markdown.StartsWith('- See what')) 'Current release notes did not start with their own first bullet.'
Check ($result.Markdown.Contains($arrow + ' details.')) 'Unicode release notes changed.'
Check ($result.Markdown.Contains('### More detail') -and $result.Markdown.Contains('Preserve this last current-version bullet.')) 'A subheading or fenced sample truncated the release notes.'
Check (!$result.Markdown.Contains('Do not borrow')) 'Extraction borrowed another version or matched a version prefix.'
Check ([IO.File]::ReadAllText($output).Trim() -ceq $result.Markdown) 'Written notes do not match extracted Markdown.'
$unknownOutput = Join-Path $fixture 'Unknown.md'
$unknown = & $script -Version '2.0.0-qa.1' -ChangelogPath $changelog -OutputPath $unknownOutput
Check ($null -eq $unknown -and !(Test-Path -LiteralPath $unknownOutput)) 'Unknown versions must have no invented inline notes.'
ExpectFailure { & $script -Version '1.0.10' -ChangelogPath $changelog -OutputPath $output } 'Existing release notes output was overwritten.'
ExpectFailure { & $script -Version '1.0.10' -ChangelogPath $changelog -OutputPath $changelog } 'Release notes overwrote their changelog source.'

$duplicate = Join-Path $fixture 'duplicate.md'
"## 1.0.10`n- First.`n## 1.0.10`n- Second." | Set-Content -LiteralPath $duplicate -Encoding utf8
ExpectFailure { & $script -Version '1.0.10' -ChangelogPath $duplicate -OutputPath (Join-Path $fixture 'DuplicateOutput.md') } 'Duplicate version sections were accepted.'
$empty = Join-Path $fixture 'empty.md'
"## 1.0.10`n`n## 1.0.9`n- Older." | Set-Content -LiteralPath $empty -Encoding utf8
ExpectFailure { & $script -Version '1.0.10' -ChangelogPath $empty -OutputPath (Join-Path $fixture 'EmptyOutput.md') } 'Empty matching release notes were accepted.'

$explicit = Join-Path $fixture 'ExplicitSource.md'
[IO.File]::WriteAllText($explicit, "- Custom version changes.`r`n- Keep XML characters <tag> & UTF-8 $umlaut.`r`n")
$custom = & $script -Version '2.0.0-qa.1' -SourcePath $explicit -OutputPath (Join-Path $fixture 'Custom.md')
Check ($custom.Version -ceq '2.0.0-qa.1' -and $custom.Markdown.Contains('<tag> & UTF-8 ' + $umlaut + '.')) 'Explicit custom-version notes did not preserve source text.'
Check (!$custom.Markdown.Contains("`r")) 'Release notes line endings were not normalized.'
$blank = Join-Path $fixture 'Blank.md'
[IO.File]::WriteAllText($blank, ' ')
ExpectFailure { & $script -Version '2.0.0-qa.1' -SourcePath $blank -OutputPath (Join-Path $fixture 'BlankOutput.md') } 'Explicit blank notes were accepted.'
ExpectFailure { & $script -Version '2.0.0-qa.1' -SourcePath (Join-Path $fixture 'Missing.md') -OutputPath (Join-Path $fixture 'MissingOutput.md') } 'Missing explicit source was accepted.'
$large = Join-Path $fixture 'Large.md'
[IO.File]::WriteAllText($large, ('x' * 131073))
ExpectFailure { & $script -Version '2.0.0-qa.1' -SourcePath $large -OutputPath (Join-Path $fixture 'LargeOutput.md') } 'Oversized notes were accepted.'

$live = & $script -Version '1.0.10' -OutputPath (Join-Path $fixture 'Repository.md')
Check ($live.Markdown.StartsWith("- See what's new") -and !$live.Markdown.Contains('## 1.0.9')) 'Current repository release notes did not match the version.'
Write-Output "PASS: $checks isolated release-note extraction checks. Fixtures: $fixture"
