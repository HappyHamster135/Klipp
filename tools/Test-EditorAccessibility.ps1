[CmdletBinding()]
param(
    [string]$RepositoryRoot
)

$ErrorActionPreference = 'Stop'
if ([string]::IsNullOrWhiteSpace($RepositoryRoot)) {
    $RepositoryRoot = Split-Path -Parent $PSScriptRoot
}
$desktopRoot = Join-Path $RepositoryRoot 'src\Klipp.Desktop'
$themePath = Join-Path $desktopRoot 'Resources\Theme.xaml'
$trimPath = Join-Path $desktopRoot 'Controls\TrimRangeSelector.xaml'
$dialogFactoryPath = Join-Path $desktopRoot 'Services\AccessibleDialogFactory.cs'
$failures = [System.Collections.Generic.List[string]]::new()

$sourceFiles = Get-ChildItem -LiteralPath $desktopRoot -Recurse -File |
    Where-Object {
        $_.FullName -notmatch '[\\/]obj[\\/]' -and
        $_.Extension -in '.xaml', '.cs'
    }

$forcedDarkTheme = $sourceFiles | Select-String -SimpleMatch 'RequestedTheme="Dark"'
if ($forcedDarkTheme) {
    $failures.Add('RequestedTheme="Dark" får inte användas eftersom det blockerar systemets high-contrast-läge.')
}

$dialogConstructors = $sourceFiles |
    Where-Object { $_.FullName -ne $dialogFactoryPath } |
    Select-String -SimpleMatch 'new ContentDialog'
if ($dialogConstructors) {
    $failures.Add('ContentDialog ska skapas via AccessibleDialogFactory.')
}

$factoryCallCount = ($sourceFiles | Select-String -SimpleMatch 'AccessibleDialogFactory.Create(').Count
if ($factoryCallCount -lt 11) {
    $failures.Add("Förväntade minst 11 granskade dialoganrop men hittade $factoryCallCount.")
}

$dialogFactory = Get-Content -LiteralPath $dialogFactoryPath -Raw
if ($dialogFactory -match '(?s)new ContentDialog\s*\{[^}]*\bMaxWidth\s*=') {
    $failures.Add('ContentDialog.MaxWidth constrains the modal overlay. Limit the inner dialog surface with ContentDialogMaxWidth instead.')
}
foreach ($requiredDialogToken in @(
    'dialog.Resources["ContentDialogMaxWidth"]',
    'contentElement.MaxWidth = DialogContentMaxWidth',
    'contentElement.MaxHeight = DialogContentMaxHeight'
)) {
    if ($dialogFactory.IndexOf($requiredDialogToken, [System.StringComparison]::Ordinal) -lt 0) {
        $failures.Add("Dialog layout constraint missing: $requiredDialogToken")
    }
}

$theme = Get-Content -LiteralPath $themePath -Raw
foreach ($requiredThemeToken in @(
    'x:Key="HighContrast"',
    'SystemColorWindowColor',
    'SystemColorWindowTextColor',
    'SystemColorHighlightColor',
    'Color="{ThemeResource KlippBackgroundColor}"',
    'Color="{ThemeResource KlippTextPrimaryColor}"'
)) {
    if ($theme.IndexOf($requiredThemeToken, [System.StringComparison]::Ordinal) -lt 0) {
        $failures.Add("Temaresurs saknas: $requiredThemeToken")
    }
}

$trim = Get-Content -LiteralPath $trimPath -Raw
foreach ($requiredTrimToken in @(
    'x:Name="StartNumberBox"',
    'x:Name="EndNumberBox"',
    'AutomationProperties.Name="Trim start in seconds"',
    'AutomationProperties.Name="Trim end in seconds"'
)) {
    if ($trim.IndexOf($requiredTrimToken, [System.StringComparison]::Ordinal) -lt 0) {
        $failures.Add("Tangentbordsstöd för trimning saknas: $requiredTrimToken")
    }
}

if ($failures.Count -gt 0) {
    Write-Error ("Editor accessibility audit failed:`n- " + ($failures -join "`n- "))
    exit 1
}

Write-Host "Editor accessibility audit passed: $factoryCallCount dialoger, high contrast och tangentbordstrimning verifierade."
