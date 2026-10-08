param([string]$OutputName = '')
$ErrorActionPreference = 'Stop'
$root = [System.IO.Path]::GetFullPath($PSScriptRoot)
$solution = Join-Path $root 'NekoPlayer.sln'
$project = Join-Path $root 'src\NekoPlayer.App\NekoPlayer.App.csproj'
$artifactsDirectory = Join-Path $root 'artifacts'
$ffmpegSource = Join-Path $root 'tools\ffmpeg'
$gatewaySource = Join-Path $root 'tools\music-gateway'
$nodeSource = Join-Path $root 'tools\node'
[xml]$projectXml = Get-Content -LiteralPath $project -Raw
$version = [string]($projectXml.Project.PropertyGroup.Version | Where-Object { $_ } | Select-Object -First 1)
if ($version -notmatch '^\d+\.\d+\.\d+$') { throw 'Application Version must be a three-part release version.' }
if ([string]::IsNullOrWhiteSpace($OutputName)) { $OutputName = "win-x64-v$version" }
if ($OutputName -ne "win-x64-v$version") { throw "Publish must use the versioned win-x64-v$version directory. Use install-release.ps1 to update the installed win-x64 directory." }
$publishDirectory = Join-Path (Join-Path $artifactsDirectory 'publish') $OutputName
$targetFramework = [string]($projectXml.Project.PropertyGroup.TargetFramework | Where-Object { $_ } | Select-Object -First 1)
$minimumWindowsVersion = [string]($projectXml.Project.PropertyGroup.SupportedOSPlatformVersion | Where-Object { $_ } | Select-Object -First 1)
if ($targetFramework -ne 'net8.0-windows10.0.19041.0' -or $minimumWindowsVersion -ne '10.0.17763.0') { throw 'The Windows release must target Windows SDK 19041 with Windows 10 1809 (17763) minimum support.' }
$fileVersion = "$version.0"
$nodeVersion = 'v24.21.0'
$nodeArchiveSha256 = '158f7685b44de51f6c0df1d153526cbcd3e1bc739a8dfc607721cef75de9e541'

function Invoke-Step([string]$Name, [scriptblock]$Action) {
    Write-Host "`n== $Name ==" -ForegroundColor Cyan
    & $Action
    if ($LASTEXITCODE -ne 0) { throw "$Name failed with exit code $LASTEXITCODE" }
}

function Test-FfmpegRuntime([string]$Directory) {
    if (-not (Test-Path -LiteralPath (Join-Path $Directory 'ffmpeg.exe'))) { return $false }
    if (-not (Test-Path -LiteralPath (Join-Path $Directory 'ffprobe.exe'))) { return $false }
    foreach ($pattern in @('avcodec-*.dll', 'avformat-*.dll', 'avutil-*.dll', 'swresample-*.dll')) {
        if (-not (Get-ChildItem -LiteralPath $Directory -Filter $pattern -File -ErrorAction SilentlyContinue | Select-Object -First 1)) { return $false }
    }
    return $true
}

function Test-MusicGatewayRuntime([string]$GatewayDirectory, [string]$NodeDirectory) {
    foreach ($required in @('server.mjs', 'src\index.js', 'package.json', 'package-lock.json', 'node_modules', 'LICENSES\THIRD-PARTY-NOTICES.json', 'LICENSES\THIRD-PARTY-NOTICES.md')) {
        if (-not (Test-Path -LiteralPath (Join-Path $GatewayDirectory $required))) { throw "Music gateway is missing $required. Run setup-music-gateway.ps1 first." }
    }
    foreach ($required in @('node.exe', 'LICENSE', 'metadata.json', 'SOURCE.txt', 'SHASUMS256.txt')) {
        if (-not (Test-Path -LiteralPath (Join-Path $NodeDirectory $required))) { throw "Bundled Node.js is missing $required. Run setup-music-gateway.ps1 first." }
    }
    $metadata = Get-Content -LiteralPath (Join-Path $NodeDirectory 'metadata.json') -Raw | ConvertFrom-Json
    if ($metadata.NodeVersion -ne $nodeVersion -or $metadata.ArchiveSha256 -ne $nodeArchiveSha256 -or -not $metadata.OfficialSha256Verified) { throw 'Bundled Node.js provenance is invalid.' }
    foreach ($entry in @(@{ File = 'node.exe'; Hash = $metadata.NodeExeSha256 }, @{ File = 'LICENSE'; Hash = $metadata.LicenseSha256 })) {
        if ((Get-FileHash -LiteralPath (Join-Path $NodeDirectory $entry.File) -Algorithm SHA256).Hash.ToLowerInvariant() -ne $entry.Hash) { throw "Bundled Node.js $($entry.File) hash does not match metadata." }
    }
    $actualVersion = & (Join-Path $NodeDirectory 'node.exe') --version
    if ($LASTEXITCODE -ne 0 -or [string]$actualVersion -ne $nodeVersion) { throw 'Bundled Node.js version validation failed.' }
    & (Join-Path $NodeDirectory 'node.exe') --check (Join-Path $GatewayDirectory 'server.mjs')
    if ($LASTEXITCODE -ne 0) { throw 'Music gateway syntax validation failed.' }
}

function Copy-RuntimeTree([string]$Source, [string]$Destination) {
    $sourceFullPath = [System.IO.Path]::GetFullPath($Source).TrimEnd('\', '/')
    New-Item -ItemType Directory -Path $Destination -Force | Out-Null
    foreach ($file in Get-ChildItem -LiteralPath $sourceFullPath -File -Recurse -Force) {
        if ($file.Attributes -band [System.IO.FileAttributes]::ReparsePoint) { throw "Runtime source must not contain file links: $($file.FullName)" }
        $relative = $file.FullName.Substring($sourceFullPath.Length + 1)
        # Only packaged source trees are considered; fixtures, local state and credentials are excluded.
        if ($relative -match '(^|[\\/])(\.git|\.github|\.vscode|\.idea|\.cache|\.bin|tests?|fixtures?|testfixtures|examples?|downloads|credentials)([\\/]|$)') { continue }
        if ($file.Name -match '^\.env($|\.)|^(cookies?|credentials)\.json$' -or $file.Extension -match '^\.(mp3|wav|flac|ogg|m4a|aac|wma|aiff|db|sqlite|sqlite3|log)$') { continue }
        $target = Join-Path $Destination $relative
        New-Item -ItemType Directory -Path (Split-Path -Parent $target) -Force | Out-Null
        Copy-Item -LiteralPath $file.FullName -Destination $target -Force
    }
}

try {
    if (-not (Test-FfmpegRuntime $ffmpegSource)) {
        throw 'FFmpeg Shared runtime is missing or incomplete. Run setup-ffmpeg.ps1 first.'
    }
    Test-MusicGatewayRuntime $gatewaySource $nodeSource

    # NuGet writes one project.assets.json per project. Restore explicitly for
    # Release so Debug-only Avalonia diagnostics cannot leak into publish deps.
    Invoke-Step 'Restore NuGet packages (Release)' { dotnet restore $solution -p:Configuration=Release }
    Invoke-Step 'Build Release' { dotnet build $solution -c Release --no-restore }
    Invoke-Step 'Test Release' { dotnet test $solution -c Release --no-build }
    Invoke-Step 'Test bundled music gateway' { & (Join-Path $nodeSource 'node.exe') --test (Join-Path $gatewaySource 'tests\*.test.js') }

    $resolvedArtifacts = [System.IO.Path]::GetFullPath($artifactsDirectory)
    $resolvedPublish = [System.IO.Path]::GetFullPath($publishDirectory)
    if (-not $resolvedPublish.StartsWith($resolvedArtifacts + [System.IO.Path]::DirectorySeparatorChar, [System.StringComparison]::OrdinalIgnoreCase)) {
        throw 'Publish target escaped the artifacts directory.'
    }
    if (Test-Path -LiteralPath $resolvedPublish) { Remove-Item -LiteralPath $resolvedPublish -Recurse -Force }
    New-Item -ItemType Directory -Path $resolvedPublish -Force | Out-Null

    Invoke-Step 'Restore self-contained win-x64 runtime' { dotnet restore $project -r win-x64 -p:Configuration=Release }
    Invoke-Step 'Publish win-x64' { dotnet publish $project -c Release -r win-x64 --self-contained true --no-restore -o $resolvedPublish }

    $ffmpegTarget = Join-Path $resolvedPublish 'ffmpeg'
    New-Item -ItemType Directory -Path $ffmpegTarget -Force | Out-Null
    foreach ($file in Get-ChildItem -LiteralPath $ffmpegSource -File) {
        if ($file.Name -in @('ffmpeg.exe', 'ffprobe.exe', 'metadata.json', 'SOURCE.txt', 'README.md') -or $file.Extension -eq '.dll') {
            Copy-Item -LiteralPath $file.FullName -Destination $ffmpegTarget -Force
        }
    }
    Copy-RuntimeTree (Join-Path $ffmpegSource 'licenses') (Join-Path $ffmpegTarget 'licenses')

    $gatewayTarget = Join-Path $resolvedPublish 'music-gateway'
    New-Item -ItemType Directory -Path $gatewayTarget -Force | Out-Null
    foreach ($document in @('server.mjs', 'package.json', 'package-lock.json', 'README.md')) {
        Copy-Item -LiteralPath (Join-Path $gatewaySource $document) -Destination $gatewayTarget -Force
    }
    foreach ($directory in @('src', 'node_modules', 'LICENSES')) {
        Copy-RuntimeTree (Join-Path $gatewaySource $directory) (Join-Path $gatewayTarget $directory)
    }
    foreach ($directory in @('providers', 'vendors')) {
        if (Test-Path -LiteralPath (Join-Path $gatewaySource $directory)) { Copy-RuntimeTree (Join-Path $gatewaySource $directory) (Join-Path $gatewayTarget $directory) }
    }
    $nodeTarget = Join-Path $gatewayTarget 'node'
    New-Item -ItemType Directory -Path $nodeTarget -Force | Out-Null
    foreach ($file in @('node.exe', 'LICENSE', 'metadata.json', 'SOURCE.txt', 'SHASUMS256.txt')) {
        Copy-Item -LiteralPath (Join-Path $nodeSource $file) -Destination $nodeTarget -Force
    }

    foreach ($document in @('LICENSE', 'README.md', 'THIRD-PARTY-NOTICES.md')) {
        Copy-Item -LiteralPath (Join-Path $root $document) -Destination $resolvedPublish -Force
    }
    $publishedDocs = Join-Path $resolvedPublish 'docs'
    New-Item -ItemType Directory -Path $publishedDocs -Force | Out-Null
    foreach ($document in @("MANUAL-ACCEPTANCE-v$version.md", "RELEASE-NOTES-v$version.md")) {
        Copy-Item -LiteralPath (Join-Path (Join-Path $root 'docs') $document) -Destination $publishedDocs -Force
    }
    $publishedAssets = Join-Path $resolvedPublish 'Assets'
    New-Item -ItemType Directory -Path $publishedAssets -Force | Out-Null
    Copy-Item -LiteralPath (Join-Path $root 'src\NekoPlayer.App\Assets\AppIcon.png') -Destination $publishedAssets -Force
    Copy-Item -LiteralPath (Join-Path $root 'src\NekoPlayer.App\Assets\NekoPlayer.ico') -Destination $publishedAssets -Force

    $publishedExe = Join-Path $resolvedPublish 'NekoPlayer.exe'
    if (-not (Test-Path -LiteralPath $publishedExe)) { throw 'NekoPlayer.exe is missing after publish.' }
    if (-not (Test-FfmpegRuntime $ffmpegTarget)) { throw 'Published FFmpeg Shared runtime is incomplete.' }
    $publishedVersion = (Get-Item -LiteralPath $publishedExe).VersionInfo
    if ($publishedVersion.FileVersion -ne $fileVersion -or $publishedVersion.ProductVersion -ne $version) {
        throw "Published application version is incorrect: FileVersion=$($publishedVersion.FileVersion), ProductVersion=$($publishedVersion.ProductVersion)"
    }
    $publishedDeps = Join-Path $resolvedPublish 'NekoPlayer.deps.json'
    if (-not (Test-Path -LiteralPath $publishedDeps)) { throw 'NekoPlayer.deps.json is missing after publish.' }
    if (Select-String -LiteralPath $publishedDeps -Pattern 'Avalonia.Diagnostics' -Quiet) { throw 'Release dependency graph contains Avalonia.Diagnostics.' }
    if (Get-ChildItem -LiteralPath $resolvedPublish -File -Recurse -Filter 'Avalonia.Diagnostics*') { throw 'Release package contains Avalonia.Diagnostics files.' }
    Test-MusicGatewayRuntime $gatewayTarget $nodeTarget
    $publishedAppIcon = Join-Path $resolvedPublish 'Assets\AppIcon.png'
    $publishedWindowsIcon = Join-Path $resolvedPublish 'Assets\NekoPlayer.ico'
    if (-not (Test-Path -LiteralPath $publishedAppIcon) -or -not (Test-Path -LiteralPath $publishedWindowsIcon)) { throw 'Published icon resources are missing.' }
    $projectText = Get-Content -LiteralPath $project -Raw
    if ($projectText -notmatch '<AvaloniaResource Include="Assets\\\*\*"') { throw 'AppIcon.png is not declared as an AvaloniaResource.' }
    Add-Type -AssemblyName System.Drawing
    $publishedIcon = [Drawing.Icon]::ExtractAssociatedIcon($publishedExe)
    if ($null -eq $publishedIcon) { throw 'Published NekoPlayer.exe does not contain an extractable Windows icon.' }
    $publishedIcon.Dispose()

    $publishedFfmpeg = Join-Path $ffmpegTarget 'ffmpeg.exe'
    $ffmpegVersionOutput = & $publishedFfmpeg -version 2>&1
    if ($LASTEXITCODE -ne 0) { throw 'Published ffmpeg.exe could not start.' }
    $ffmpegVersion = [string]($ffmpegVersionOutput | Select-Object -First 1)

    foreach ($file in Get-ChildItem -LiteralPath $resolvedPublish -File -Recurse -Force) {
        $relative = $file.FullName.Substring($resolvedPublish.Length + 1)
        if ($relative -match '(^|[\\/])(test-reports|fixtures?|testfixtures|credentials)([\\/]|$)|^(Data|Config|Cache|Logs|Temp)([\\/]|$)' -or
            $file.Name -match '^\.env($|\.)|^(settings|cookies?|credentials|accounts?|authorization)\.json$|^NekoPlayer\..*Verifier\.' -or
            $file.Extension -match '^\.(mp3|wav|flac|ogg|m4a|aac|wma|aiff|db|sqlite|sqlite3|log)$') {
            throw "Release package contains validation media or user state: $relative"
        }
    }

    $manifestFiles = @(Get-ChildItem -LiteralPath $resolvedPublish -File -Recurse -Force | Sort-Object FullName | ForEach-Object {
        [ordered]@{ Path = $_.FullName.Substring($resolvedPublish.Length + 1).Replace('\', '/'); Bytes = $_.Length; Sha256 = (Get-FileHash -LiteralPath $_.FullName -Algorithm SHA256).Hash.ToLowerInvariant() }
    })
    [ordered]@{
        SchemaVersion = 1; Product = 'NekoPlayer'; Version = $version; Runtime = 'win-x64'; SelfContained = $true
        TargetFramework = $targetFramework; MinimumWindowsVersion = $minimumWindowsVersion
        GeneratedAtUtc = [DateTime]::UtcNow.ToString('o'); NodeVersion = $nodeVersion
        NodeArchiveSha256 = $nodeArchiveSha256; GatewayLockSha256 = (Get-FileHash -LiteralPath (Join-Path $gatewayTarget 'package-lock.json') -Algorithm SHA256).Hash.ToLowerInvariant()
        Files = $manifestFiles
    } | ConvertTo-Json -Depth 6 | Set-Content -LiteralPath (Join-Path $resolvedPublish 'runtime-manifest.json') -Encoding utf8

    Write-Host "Application: $publishedExe" -ForegroundColor Green
    Write-Host "FFmpeg: $publishedFfmpeg" -ForegroundColor Green
    Write-Host "Application version: $version" -ForegroundColor Green
    Write-Host "FFmpeg version: $ffmpegVersion" -ForegroundColor Green
    Write-Host "Bundled Node.js: $nodeVersion; runtime-manifest.json contains $($manifestFiles.Count) file hashes" -ForegroundColor Green
} catch {
    Write-Error $_
    exit 1
}
