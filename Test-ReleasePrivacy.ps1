param(
    [string[]]$Package = @()
)

$ErrorActionPreference = 'Stop'

function Fail([string]$message) {
    Write-Error $message
    exit 1
}

function Get-TextFiles([string]$root) {
    $extensions = @(
        '.bat',
        '.cmd',
        '.config',
        '.cs',
        '.htm',
        '.html',
        '.json',
        '.md',
        '.plist',
        '.ps1',
        '.swift',
        '.txt',
        '.xml'
    )

    Get-ChildItem -LiteralPath $root -Recurse -File -Force |
        Where-Object {
            $extensions -contains $_.Extension.ToLowerInvariant() -or
            $_.Name -eq '.gitignore'
        }
}

function Test-TextFile([string]$path, [string]$displayPath) {
    $text = Get-Content -LiteralPath $path -Raw
    $bs = [string][char]92
    $privateSyncName = 'Drop' + 'box'
    $privateWorkspace = 'backups' + $bs + 'Codex'
    $privateCodexCurrent = 'Codex' + $bs + 'current'
    $privateUser = 'Onj' + 'Lo'
    $checks = [ordered]@{
        'private Windows local path' = ('(?<![A-Za-z0-9])[A-Z]:(?:' + [regex]::Escape($bs) + '|/)(?:Drop' + 'box|projects|backups|Users(?:' + [regex]::Escape($bs) + '|/)(?!Public\b))')
        'private macOS user path' = '/Users/[A-Za-z0-9_.-]+/(?:Drop' + 'box|Documents|Desktop|Downloads)'
        'local user profile path' = ('Users' + [regex]::Escape($bs) + '(?!Public\b)[A-Za-z0-9_.-]+')
        'private sync/workspace path wording' = ([regex]::Escape($privateSyncName) + '|' + [regex]::Escape($privateWorkspace) + '|' + [regex]::Escape($privateCodexCurrent))
        'local user name' = ('(?<![A-Za-z0-9])' + [regex]::Escape($privateUser) + '(?![A-Za-z0-9])')
        'token loaded from a file in public package' = ('GH_TOKEN\s*=.*Get-Content|GITHUB_TOKEN\s*=.*Get-Content|' + 'token' + '\.txt|shared' + 'Token' + 'File')
    }

    foreach ($description in $checks.Keys) {
        if ($text -match $checks[$description]) {
            Fail "$displayPath contains forbidden private/personal release text: $description"
        }
    }
}

function Test-Directory([string]$root, [string]$label) {
    if (-not (Test-Path -LiteralPath $root)) {
        Fail "$label does not exist: $root"
    }

    foreach ($file in Get-TextFiles $root) {
        $relative = $file.FullName.Substring(([IO.Path]::GetFullPath($root).TrimEnd('\', '/') + [IO.Path]::DirectorySeparatorChar).Length)
        Test-TextFile $file.FullName "$label\$relative"
    }
}

function Test-Zip([string]$zipPath) {
    if (-not (Test-Path -LiteralPath $zipPath)) {
        Fail "Package ZIP does not exist: $zipPath"
    }

    $extractRoot = Join-Path ([IO.Path]::GetTempPath()) ('NativeSerials-release-privacy-' + [guid]::NewGuid().ToString('N'))
    New-Item -ItemType Directory -Force -Path $extractRoot | Out-Null
    try {
        Expand-Archive -LiteralPath $zipPath -DestinationPath $extractRoot -Force
        if ((Split-Path -Leaf $zipPath) -notmatch '(?i)source') {
            $readme = Get-ChildItem -LiteralPath $extractRoot -Recurse -File -Force |
                Where-Object { $_.Name -ieq 'README.md' } |
                Select-Object -First 1
            if ($readme) {
                Fail "Program package must not ship README.md; use Manual.html as the user-facing document. Found: $($readme.FullName)"
            }
        }
        Test-Directory $extractRoot 'package ZIP'
    }
    finally {
        Remove-Item -LiteralPath $extractRoot -Recurse -Force -ErrorAction SilentlyContinue
    }
}

foreach ($item in $Package) {
    Test-Zip $item
}

Write-Host 'Native Serials release privacy check passed.'
