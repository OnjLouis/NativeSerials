param(
    [string]$OutputPath = "$PSScriptRoot\portable\NativeSerials.exe"
)

$ErrorActionPreference = 'Stop'
$version = '1.0'

$csc = Join-Path $env:WINDIR 'Microsoft.NET\Framework64\v4.0.30319\csc.exe'
if (-not (Test-Path -LiteralPath $csc)) {
    throw "Could not find the .NET Framework C# compiler at $csc"
}

$portable = Join-Path $PSScriptRoot 'portable'
New-Item -ItemType Directory -Force -Path $portable | Out-Null
Remove-Item -LiteralPath (Join-Path $portable 'README.md') -Force -ErrorAction SilentlyContinue

$references = @(
    'System.dll',
    'System.Core.dll',
    'System.Drawing.dll',
    'System.IO.Compression.dll',
    'System.IO.Compression.FileSystem.dll',
    'System.Web.Extensions.dll',
    'System.Windows.Forms.dll',
    'System.Xml.dll',
    'System.Xml.Linq.dll'
) -join ','

$sources = Get-ChildItem -LiteralPath (Join-Path $PSScriptRoot 'src') -Filter '*.cs' | Sort-Object Name | ForEach-Object { $_.FullName }
& $csc /nologo /target:winexe /platform:x64 /out:$OutputPath /reference:$references $sources
if ($LASTEXITCODE -ne 0) {
    throw "Build failed with exit code $LASTEXITCODE"
}

Copy-Item -LiteralPath (Join-Path $PSScriptRoot '..\Manual.html') -Destination (Join-Path $portable 'Manual.html') -Force
Compress-Archive -Path (Join-Path $portable '*') -DestinationPath (Join-Path $PSScriptRoot "NativeSerialsWindows-$version.zip") -Force
Write-Host "Built $OutputPath"
Write-Host "Built $(Join-Path $PSScriptRoot "NativeSerialsWindows-$version.zip")"
