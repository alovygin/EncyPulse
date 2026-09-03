<#
  Rename the template extension in one go:
    .\rename.ps1 -Name MyCoolExtension
  Replaces "EncyExtension" in file names and file contents under src\.
#>
param(
    [Parameter(Mandatory)]
    [ValidatePattern('^[A-Za-z][A-Za-z0-9.]*$')]
    [string]$Name
)
$ErrorActionPreference = 'Stop'
$src = Join-Path $PSScriptRoot 'src'

Get-ChildItem $src -File | ForEach-Object {
    $content = Get-Content -Raw -LiteralPath $_.FullName
    if ($content -match 'EncyExtension') {
        Set-Content -LiteralPath $_.FullName -Value ($content -replace 'EncyExtension', $Name) -Encoding utf8 -NoNewline
    }
    if ($_.Name -match 'EncyExtension') {
        Rename-Item -LiteralPath $_.FullName -NewName ($_.Name -replace 'EncyExtension', $Name)
    }
}
Write-Host "Renamed to $Name. Check src\, then commit."
