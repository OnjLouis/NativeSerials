param(
    [string]$PackageDirectory = $(if ([string]::IsNullOrWhiteSpace($env:NATIVE_SERIALS_WINDOWS_PACKAGE_DIR)) { Join-Path ([IO.Path]::GetTempPath()) 'NativeSerials-Windows' } else { $env:NATIVE_SERIALS_WINDOWS_PACKAGE_DIR }),
    [string]$OutputPath = '',
    [string]$Dotnet = 'dotnet'
)

$ErrorActionPreference = 'Stop'
$version = '1.1.1'
$PackageDirectory = [IO.Path]::GetFullPath($PackageDirectory)
$repoRoot = [IO.Path]::GetFullPath((Join-Path $PSScriptRoot '..')).TrimEnd('\')
if ($PackageDirectory.TrimEnd('\').StartsWith($repoRoot + '\', [StringComparison]::OrdinalIgnoreCase)) {
    throw "PackageDirectory must be outside the source repository: $PackageDirectory"
}
if ([string]::IsNullOrWhiteSpace($OutputPath)) {
    $OutputPath = Join-Path $PackageDirectory 'NativeSerials.exe'
} else {
    $OutputPath = [IO.Path]::GetFullPath($OutputPath)
    $outputParent = [IO.Path]::GetDirectoryName($OutputPath).TrimEnd('\')
    if (-not $outputParent.Equals($PackageDirectory.TrimEnd('\'), [StringComparison]::OrdinalIgnoreCase)) {
        throw "OutputPath must be directly inside PackageDirectory: $OutputPath"
    }
}

$csc = Join-Path $env:WINDIR 'Microsoft.NET\Framework64\v4.0.30319\csc.exe'
if (-not (Test-Path -LiteralPath $csc)) {
    throw "Could not find the .NET Framework C# compiler at $csc"
}

$portable = $PackageDirectory
Remove-Item -LiteralPath $portable -Recurse -Force -ErrorAction SilentlyContinue
New-Item -ItemType Directory -Force -Path $portable | Out-Null
Remove-Item -LiteralPath (Join-Path $portable 'README.md') -Force -ErrorAction SilentlyContinue

$coreOutput = Join-Path ([IO.Path]::GetDirectoryName($portable)) 'UpdaterCore'
& $Dotnet build (Join-Path $repoRoot 'ThirdParty\Onj.SignedUpdater\Onj.SignedUpdater.csproj') -t:Rebuild -c Release -f net48 -p:TargetFrameworks=net48 "-p:ArtifactsPath=$coreOutput" -p:DebugType=None -p:DebugSymbols=false -p:UseSharedCompilation=false -nodeReuse:false --nologo
if ($LASTEXITCODE -ne 0) { throw 'Signed updater library build failed.' }
$updaterLibrary = Join-Path $coreOutput 'bin\Onj.SignedUpdater\release_net48\Onj.SignedUpdater.dll'
if (-not (Test-Path -LiteralPath $updaterLibrary)) { throw 'Signed updater library output is missing.' }

$references = @(
    'System.dll',
    'System.Core.dll',
    'System.Drawing.dll',
    'System.IO.Compression.dll',
    'System.IO.Compression.FileSystem.dll',
    'System.Web.Extensions.dll',
    'System.Windows.Forms.dll',
    'System.Xml.dll',
    'System.Xml.Linq.dll',
    'System.Net.Http.dll',
    'System.Runtime.Serialization.dll',
    $updaterLibrary
) -join ','

$sources = Get-ChildItem -LiteralPath (Join-Path $PSScriptRoot 'src') -Filter '*.cs' | Sort-Object Name | ForEach-Object { $_.FullName }
& $csc /nologo /target:winexe /platform:x64 /out:$OutputPath /reference:$references "/resource:$repoRoot\Resources\UpdatePublicKey.xml,NativeSerials.UpdatePublicKey.xml" $sources
if ($LASTEXITCODE -ne 0) {
    throw "Build failed with exit code $LASTEXITCODE"
}

Copy-Item -LiteralPath (Join-Path $PSScriptRoot '..\Manual.html') -Destination (Join-Path $portable 'Manual.html') -Force
Copy-Item -LiteralPath $updaterLibrary -Destination $portable
Copy-Item -LiteralPath (Join-Path $repoRoot 'ThirdParty\Onj.SignedUpdater\LICENSE') -Destination (Join-Path $portable 'ThirdPartyNotices.txt')
$zipPath = Join-Path ([IO.Path]::GetDirectoryName($portable)) "NativeSerialsWindows-$version.zip"
Compress-Archive -Path (Join-Path $portable '*') -DestinationPath $zipPath -Force
Write-Host "Built $OutputPath"
Write-Host "Built $zipPath"
