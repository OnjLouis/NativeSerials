param(
    [string]$Version = '1.0',
    [string]$ReleaseRoot = (Join-Path $PSScriptRoot '..\..\..\NativeSerials')
)

$ErrorActionPreference = 'Stop'

function Fail([string]$message) {
    Write-Error $message
    exit 1
}

function Assert-File([string]$path, [string]$label) {
    if (-not (Test-Path -LiteralPath $path -PathType Leaf)) {
        Fail "$label not found: $path"
    }
}

function Get-ZipEntries([string]$zipPath) {
    Add-Type -AssemblyName System.IO.Compression.FileSystem
    $zip = [IO.Compression.ZipFile]::OpenRead($zipPath)
    try {
        return @($zip.Entries | ForEach-Object { $_.FullName })
    }
    finally {
        $zip.Dispose()
    }
}

function Assert-ContainsEntry([string[]]$entries, [string]$pattern, [string]$label) {
    if (-not ($entries | Where-Object { $_ -like $pattern } | Select-Object -First 1)) {
        Fail "$label missing ZIP entry matching: $pattern"
    }
}

function Assert-NotContainsEntry([string[]]$entries, [string]$pattern, [string]$label) {
    if ($entries | Where-Object { $_ -like $pattern } | Select-Object -First 1) {
        Fail "$label contains forbidden ZIP entry matching: $pattern"
    }
}

$programBuilds = Join-Path $ReleaseRoot 'Program Builds'
$sourceSnapshots = Join-Path $ReleaseRoot 'Source Snapshots'
$macZip = Join-Path $programBuilds "NativeSerialsMac-$Version.zip"
$windowsZip = Join-Path $programBuilds "NativeSerialsWindows-$Version.zip"
$sourceZip = Join-Path $sourceSnapshots "NativeSerials-source-$Version.zip"

Assert-File $macZip 'Mac program ZIP'
Assert-File $windowsZip 'Windows program ZIP'
Assert-File $sourceZip 'Source snapshot ZIP'

$macEntries = Get-ZipEntries $macZip
Assert-ContainsEntry $macEntries 'Native Serials.app/Contents/MacOS/NativeSerials' 'Mac program ZIP'
Assert-ContainsEntry $macEntries 'Native Serials.app/Contents/Resources/Manual.html' 'Mac program ZIP'
Assert-NotContainsEntry $macEntries '*README.md' 'Mac program ZIP'
Assert-NotContainsEntry $macEntries '*HANDOVER*' 'Mac program ZIP'

$windowsEntries = Get-ZipEntries $windowsZip
Assert-ContainsEntry $windowsEntries 'NativeSerials.exe' 'Windows program ZIP'
Assert-ContainsEntry $windowsEntries 'Manual.html' 'Windows program ZIP'
Assert-NotContainsEntry $windowsEntries '*README.md' 'Windows program ZIP'
Assert-NotContainsEntry $windowsEntries '*HANDOVER*' 'Windows program ZIP'

$sourceEntries = Get-ZipEntries $sourceZip
Assert-ContainsEntry $sourceEntries 'Mac/Sources/NativeSerials/AppController.swift' 'Source snapshot ZIP'
Assert-ContainsEntry $sourceEntries 'Mac/Sources/NativeSerials/NativeScanner.swift' 'Source snapshot ZIP'
Assert-ContainsEntry $sourceEntries 'Windows/src/Program.cs' 'Source snapshot ZIP'
Assert-ContainsEntry $sourceEntries 'Windows/src/UpdateService.cs' 'Source snapshot ZIP'
Assert-ContainsEntry $sourceEntries 'Manual.html' 'Source snapshot ZIP'
Assert-ContainsEntry $sourceEntries 'SmokeTest.ps1' 'Source snapshot ZIP'
Assert-ContainsEntry $sourceEntries 'Test-ReleasePrivacy.ps1' 'Source snapshot ZIP'
Assert-NotContainsEntry $sourceEntries '*HANDOVER*' 'Source snapshot ZIP'
Assert-NotContainsEntry $sourceEntries 'Mac/dist/*' 'Source snapshot ZIP'
Assert-NotContainsEntry $sourceEntries 'Windows/portable/*' 'Source snapshot ZIP'

$privacyTest = Join-Path $PSScriptRoot 'Test-ReleasePrivacy.ps1'
Assert-File $privacyTest 'Release privacy test'
& $privacyTest -Package $macZip, $windowsZip, $sourceZip

Write-Host "Native Serials smoke test passed for version $Version."

