function Write-KlippNativeVersionHeader {
    param(
        [Parameter(Mandatory = $true)]
        [ValidatePattern('^\d+\.\d+\.\d+(?:-[0-9A-Za-z.-]+)?$')]
        [string]$Version,
        [Parameter(Mandatory = $true)]
        [ValidateSet('Klipp.AudioEffects.dll', 'Klipp.GameHook.dll')]
        [string]$FileName,
        [Parameter(Mandatory = $true)]
        [string]$OutputDirectory
    )

    $versionParts = ($Version -split '-', 2)[0] -split '\.'
    foreach ($part in $versionParts) {
        if ([long]$part -gt 65535) { throw 'Native Windows version components must be between 0 and 65535.' }
    }
    $numericParts = @($versionParts | ForEach-Object { [int]$_ }) + @(0)
    $description = if ($FileName -eq 'Klipp.AudioEffects.dll') { 'Klipp audio playback effects' } else { 'Klipp game capture' }
    $content = @"
#define KLIPP_VERSION_NUMERIC $($numericParts -join ',')
#define KLIPP_FILE_VERSION "$($numericParts -join '.')"
#define KLIPP_PRODUCT_VERSION "$Version"
#define KLIPP_FILE_DESCRIPTION "$description"
#define KLIPP_ORIGINAL_FILENAME "$FileName"
"@
    $content += [Environment]::NewLine
    $headerPath = Join-Path $OutputDirectory 'Klipp.Version.generated.h'
    if (!(Test-Path -LiteralPath $headerPath) -or [IO.File]::ReadAllText($headerPath) -ne $content) {
        [IO.File]::WriteAllText($headerPath, $content, [Text.Encoding]::ASCII)
    }
}
