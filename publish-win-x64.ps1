param([switch]$SkipTests)

$ErrorActionPreference = 'Stop'
$root = $PSScriptRoot
$version = '1.0.0'
$solution = Join-Path $root 'NekoPlayer.sln'
$project = Join-Path $root 'src\NekoPlayer.App\NekoPlayer.App.csproj'
$artifactsDirectory = Join-Path $root 'artifacts'
$publishDirectory = Join-Path $artifactsDirectory 'publish\win-x64'
$releaseDirectory = Join-Path $artifactsDirectory 'release'
$archivePath = Join-Path $releaseDirectory "NekoPlayer-v$version-win-x64.zip"
$verificationDirectory = Join-Path $artifactsDirectory 'verification\win-x64-archive'
$ffmpegSource = Join-Path $root 'tools\ffmpeg'

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

function Assert-SafeOutputPath([string]$Path) {
    $artifacts = [IO.Path]::GetFullPath($artifactsDirectory)
    $target = [IO.Path]::GetFullPath($Path)
    if (-not $target.StartsWith($artifacts + [IO.Path]::DirectorySeparatorChar, [StringComparison]::OrdinalIgnoreCase)) {
        throw "Output target escaped the artifacts directory: $target"
    }
}

function Assert-CleanRelease([string]$Directory) {
    $forbidden = Get-ChildItem -LiteralPath $Directory -Recurse -File | Where-Object {
        $_.Extension.ToLowerInvariant() -in @('.pdb', '.db', '.sqlite', '.sqlite3', '.mp3', '.flac', '.wav', '.m4a', '.aac', '.ogg', '.opus', '.wma', '.ape', '.log', '.dmp')
    }
    if ($forbidden) { throw "Forbidden release files: $($forbidden.FullName -join ', ')" }
    $deps = Join-Path $Directory 'NekoPlayer.deps.json'
    if (Select-String -LiteralPath $deps -Pattern 'Avalonia.Diagnostics' -Quiet) { throw 'Release dependency graph contains Avalonia.Diagnostics.' }
    $textFiles = Get-ChildItem -LiteralPath $Directory -Recurse -File | Where-Object { $_.Extension -in @('.json', '.config', '.md', '.txt', '.xml', '.ps1', '.sh') }
    $backslash = [regex]::Escape([IO.Path]::DirectorySeparatorChar)
    $privatePattern = "C:${backslash}Users${backslash}|C:/Users/|C:${backslash}codex" + '_tmp|C:/codex' + '_tmp|O:' + $backslash + '|O:' + '/'
    $privateMatches = @($textFiles | Select-String -Pattern $privatePattern)
    if ($privateMatches.Count -gt 0) { throw 'Release contains a local absolute path.' }
}

try {
    if (-not (Test-FfmpegRuntime $ffmpegSource)) {
        & (Join-Path $root 'setup-ffmpeg.ps1')
        if ($LASTEXITCODE -ne 0 -or -not (Test-FfmpegRuntime $ffmpegSource)) { throw 'FFmpeg Shared setup failed.' }
    }

    Invoke-Step 'Restore Release dependencies' { dotnet restore $solution -p:Configuration=Release }
    Invoke-Step 'Build Release' { dotnet build $solution -c Release --no-restore }
    if (-not $SkipTests) { Invoke-Step 'Test Release' { dotnet test $solution -c Release --no-build --logger 'console;verbosity=minimal' } }

    Assert-SafeOutputPath $publishDirectory
    Assert-SafeOutputPath $releaseDirectory
    Assert-SafeOutputPath $verificationDirectory
    foreach ($path in @($publishDirectory, $archivePath, $verificationDirectory)) {
        if (Test-Path -LiteralPath $path) { Remove-Item -LiteralPath $path -Recurse -Force }
    }
    New-Item -ItemType Directory -Path $publishDirectory, $releaseDirectory -Force | Out-Null

    Invoke-Step 'Publish win-x64 self-contained' {
        dotnet publish $project -c Release -r win-x64 --self-contained true --no-restore -o $publishDirectory -p:PublishTrimmed=false -p:DebugType=None -p:DebugSymbols=false
    }

    $ffmpegTarget = Join-Path $publishDirectory 'ffmpeg'
    New-Item -ItemType Directory -Path $ffmpegTarget -Force | Out-Null
    Copy-Item -Path (Join-Path $ffmpegSource '*') -Destination $ffmpegTarget -Recurse -Force
    foreach ($document in @('LICENSE', 'README.md', 'THIRD-PARTY-NOTICES.md')) {
        Copy-Item -LiteralPath (Join-Path $root $document) -Destination $publishDirectory -Force
    }
    $assetDirectory = Join-Path $publishDirectory 'Assets'
    New-Item -ItemType Directory -Path $assetDirectory -Force | Out-Null
    Copy-Item -LiteralPath (Join-Path $root 'src\NekoPlayer.App\Assets\AppIcon.png') -Destination $assetDirectory -Force
    Copy-Item -LiteralPath (Join-Path $root 'src\NekoPlayer.App\Assets\NekoPlayer.ico') -Destination $assetDirectory -Force

    $application = Join-Path $publishDirectory 'NekoPlayer.exe'
    if (-not (Test-Path -LiteralPath $application)) { throw 'NekoPlayer.exe is missing.' }
    if (-not (Test-FfmpegRuntime $ffmpegTarget)) { throw 'Published FFmpeg Shared runtime is incomplete.' }
    $version = (Get-Item -LiteralPath $application).VersionInfo
    if ($version.FileVersion -ne '1.0.0.0' -or $version.ProductVersion -ne '1.0.0') {
        throw "Incorrect application version: FileVersion=$($version.FileVersion), ProductVersion=$($version.ProductVersion)"
    }
    foreach ($required in @('README.md', 'LICENSE', 'THIRD-PARTY-NOTICES.md', 'Assets\AppIcon.png', 'Assets\NekoPlayer.ico', 'NekoPlayer.deps.json')) {
        if (-not (Test-Path -LiteralPath (Join-Path $publishDirectory $required))) { throw "Missing release file: $required" }
    }
    Assert-CleanRelease $publishDirectory

    Compress-Archive -Path (Join-Path $publishDirectory '*') -DestinationPath $archivePath -CompressionLevel Optimal
    Expand-Archive -LiteralPath $archivePath -DestinationPath $verificationDirectory -Force
    foreach ($required in @('NekoPlayer.exe', 'README.md', 'LICENSE', 'THIRD-PARTY-NOTICES.md', 'Assets\AppIcon.png', 'Assets\NekoPlayer.ico', 'ffmpeg\ffmpeg.exe', 'ffmpeg\ffprobe.exe')) {
        if (-not (Test-Path -LiteralPath (Join-Path $verificationDirectory $required))) { throw "Archive extraction is missing: $required" }
    }
    Assert-CleanRelease $verificationDirectory
    $hash = (Get-FileHash -LiteralPath $archivePath -Algorithm SHA256).Hash.ToLowerInvariant()
    $files = Get-ChildItem -LiteralPath $publishDirectory -Recurse -File
    Write-Host "Windows archive: $archivePath" -ForegroundColor Green
    Write-Host "SHA-256: $hash" -ForegroundColor Green
    Write-Host "Files: $($files.Count); bytes: $(($files | Measure-Object Length -Sum).Sum)" -ForegroundColor Green
} catch {
    Write-Error $_
    exit 1
}
