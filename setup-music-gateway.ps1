[CmdletBinding()]
param(
    [switch]$Force,
    [switch]$SkipDependencies
)

$ErrorActionPreference = 'Stop'
$root = [System.IO.Path]::GetFullPath($PSScriptRoot)
$nodeVersion = '24.21.0'
$archiveName = "node-v$nodeVersion-win-x64.zip"
$expectedArchiveSha256 = '158f7685b44de51f6c0df1d153526cbcd3e1bc739a8dfc607721cef75de9e541'
$downloadBase = "https://nodejs.org/dist/v$nodeVersion"
$downloadsDirectory = Join-Path $root 'artifacts\downloads'
$nodeDirectory = Join-Path $root 'tools\node'
$gatewayDirectory = Join-Path $root 'tools\music-gateway'
$archivePath = Join-Path $downloadsDirectory $archiveName
$checksumsPath = Join-Path $downloadsDirectory "node-v$nodeVersion-SHASUMS256.txt"

function Assert-ChildPath([string]$Path, [string]$Parent) {
    $resolvedPath = [System.IO.Path]::GetFullPath($Path)
    $resolvedParent = [System.IO.Path]::GetFullPath($Parent).TrimEnd('\', '/')
    if (-not $resolvedPath.StartsWith($resolvedParent + [System.IO.Path]::DirectorySeparatorChar, [System.StringComparison]::OrdinalIgnoreCase)) {
        throw "Target is outside its expected directory: $resolvedPath"
    }
    return $resolvedPath
}

function Test-InstalledNode {
    $executable = Join-Path $nodeDirectory 'node.exe'
    $metadataPath = Join-Path $nodeDirectory 'metadata.json'
    if (-not (Test-Path -LiteralPath $executable) -or -not (Test-Path -LiteralPath $metadataPath) -or -not (Test-Path -LiteralPath (Join-Path $nodeDirectory 'LICENSE'))) { return $false }
    $metadata = Get-Content -LiteralPath $metadataPath -Raw | ConvertFrom-Json
    if ($metadata.NodeVersion -ne "v$nodeVersion" -or $metadata.ArchiveSha256 -ne $expectedArchiveSha256) { return $false }
    if ((Get-FileHash -LiteralPath $executable -Algorithm SHA256).Hash.ToLowerInvariant() -ne $metadata.NodeExeSha256) { return $false }
    if ((Get-FileHash -LiteralPath (Join-Path $nodeDirectory 'LICENSE') -Algorithm SHA256).Hash.ToLowerInvariant() -ne $metadata.LicenseSha256) { return $false }
    $actualVersion = & $executable --version
    return $LASTEXITCODE -eq 0 -and [string]$actualVersion -eq "v$nodeVersion"
}

New-Item -ItemType Directory -Path $downloadsDirectory -Force | Out-Null
if ($Force -or -not (Test-InstalledNode)) {
    Write-Host "Installing official Node.js v$nodeVersion Windows x64 runtime..." -ForegroundColor Cyan
    if ($Force -or -not (Test-Path -LiteralPath $checksumsPath)) {
        Invoke-WebRequest -Uri "$downloadBase/SHASUMS256.txt" -OutFile $checksumsPath
    }
    $officialHashLine = Get-Content -LiteralPath $checksumsPath | Where-Object { $_ -match ('\s+' + [regex]::Escape($archiveName) + '$') } | Select-Object -First 1
    if (-not $officialHashLine -or $officialHashLine.Split(' ', [System.StringSplitOptions]::RemoveEmptyEntries)[0] -ne $expectedArchiveSha256) {
        throw 'The official SHA-256 list does not match the pinned Node.js archive digest.'
    }
    if ($Force -or -not (Test-Path -LiteralPath $archivePath)) {
        Invoke-WebRequest -Uri "$downloadBase/$archiveName" -OutFile $archivePath
    }
    $archiveSha256 = (Get-FileHash -LiteralPath $archivePath -Algorithm SHA256).Hash.ToLowerInvariant()
    if ($archiveSha256 -ne $expectedArchiveSha256) { throw "Node.js archive SHA-256 mismatch: $archiveSha256" }

    $stagingDirectory = Assert-ChildPath (Join-Path $downloadsDirectory ('node-extract-' + [guid]::NewGuid().ToString('N'))) $downloadsDirectory
    New-Item -ItemType Directory -Path $stagingDirectory -Force | Out-Null
    try {
        Expand-Archive -LiteralPath $archivePath -DestinationPath $stagingDirectory -Force
        $extractedDirectory = Join-Path $stagingDirectory "node-v$nodeVersion-win-x64"
        foreach ($required in @('node.exe', 'LICENSE', 'node_modules\npm\bin\npm-cli.js')) {
            if (-not (Test-Path -LiteralPath (Join-Path $extractedDirectory $required))) { throw "Official Node.js archive is missing $required" }
        }
        $actualVersion = & (Join-Path $extractedDirectory 'node.exe') --version
        if ($LASTEXITCODE -ne 0 -or [string]$actualVersion -ne "v$nodeVersion") { throw 'The extracted Node.js runtime could not run with the expected version.' }
        $resolvedNode = Assert-ChildPath $nodeDirectory (Join-Path $root 'tools')
        if (Test-Path -LiteralPath $resolvedNode) { Remove-Item -LiteralPath $resolvedNode -Recurse -Force }
        New-Item -ItemType Directory -Path $resolvedNode -Force | Out-Null
        Get-ChildItem -LiteralPath $extractedDirectory -Force | Copy-Item -Destination $resolvedNode -Recurse -Force
        Copy-Item -LiteralPath $checksumsPath -Destination (Join-Path $resolvedNode 'SHASUMS256.txt') -Force
        $nodeHash = (Get-FileHash -LiteralPath (Join-Path $resolvedNode 'node.exe') -Algorithm SHA256).Hash.ToLowerInvariant()
        $licenseHash = (Get-FileHash -LiteralPath (Join-Path $resolvedNode 'LICENSE') -Algorithm SHA256).Hash.ToLowerInvariant()
        $metadata = [ordered]@{
            Source = 'https://nodejs.org/'; NodeVersion = "v$nodeVersion"; Platform = 'win-x64'
            ArchiveName = $archiveName; ArchiveUrl = "$downloadBase/$archiveName"; ChecksumsUrl = "$downloadBase/SHASUMS256.txt"
            ArchiveSha256 = $archiveSha256; OfficialSha256Verified = $true
            Verification = 'SHA-256 matched the pinned digest and the official HTTPS SHASUMS256.txt; signature verification was not performed.'
            NodeExeSha256 = $nodeHash; LicenseSha256 = $licenseHash; InstalledAtUtc = [DateTime]::UtcNow.ToString('o')
        }
        $metadata | ConvertTo-Json -Depth 5 | Set-Content -LiteralPath (Join-Path $resolvedNode 'metadata.json') -Encoding utf8
        @"
Node.js $actualVersion for Windows x64
Official source: https://nodejs.org/
Archive: $downloadBase/$archiveName
Official checksums: $downloadBase/SHASUMS256.txt
Archive SHA-256: $archiveSha256
node.exe SHA-256: $nodeHash
LICENSE SHA-256: $licenseHash
SHA-256 matches the pinned digest and the official HTTPS checksum list.
This installation does not claim verification of the checksum list's signing key.
LICENSE is the complete, unmodified file from the official ZIP, including bundled dependency notices.
"@ | Set-Content -LiteralPath (Join-Path $resolvedNode 'SOURCE.txt') -Encoding utf8
    } finally {
        $resolvedStaging = Assert-ChildPath $stagingDirectory $downloadsDirectory
        if (Test-Path -LiteralPath $resolvedStaging) { Remove-Item -LiteralPath $resolvedStaging -Recurse -Force }
    }
}

if (-not (Test-InstalledNode)) { throw 'The installed Node.js runtime failed its version/hash/license checks.' }
Write-Host "Bundled Node.js ready: $(Join-Path $nodeDirectory 'node.exe')" -ForegroundColor Green

if (-not $SkipDependencies) {
    foreach ($required in @('package.json', 'package-lock.json', 'server.mjs')) {
        if (-not (Test-Path -LiteralPath (Join-Path $gatewayDirectory $required))) { throw "Music gateway is missing $required" }
    }
    $nodeExecutable = Join-Path $nodeDirectory 'node.exe'
    $npmCli = Join-Path $nodeDirectory 'node_modules\npm\bin\npm-cli.js'
    $savedPath = $env:PATH
    Push-Location -LiteralPath $gatewayDirectory
    try {
        $env:PATH = $nodeDirectory + [System.IO.Path]::PathSeparator + $savedPath
        & $nodeExecutable $npmCli ci --omit=dev --ignore-scripts --no-audit --no-fund
        if ($LASTEXITCODE -ne 0) { throw "Music gateway npm ci failed with exit code $LASTEXITCODE" }
        & $nodeExecutable --check (Join-Path $gatewayDirectory 'server.mjs')
        if ($LASTEXITCODE -ne 0) { throw 'Music gateway server.mjs syntax validation failed.' }
    } finally {
        $env:PATH = $savedPath
        Pop-Location
    }
    Write-Host 'Music gateway dependencies installed from the lock file without lifecycle scripts.' -ForegroundColor Green
}
