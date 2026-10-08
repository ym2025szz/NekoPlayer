param(
    [switch]$Force
)

$ErrorActionPreference = 'Stop'
$root = $PSScriptRoot
$apiUrl = 'https://api.github.com/repos/BtbN/FFmpeg-Builds/releases/latest'
$toolsDirectory = Join-Path $root 'tools\ffmpeg'
$downloadsDirectory = Join-Path $root 'artifacts\downloads'
$extractDirectory = Join-Path $root 'artifacts\temp\ffmpeg-extract'
$headers = @{
    'User-Agent' = 'NekoPlayer-FFmpeg-Setup/0.1.1'
    'Accept' = 'application/vnd.github+json'
}

function Test-SharedInstallation([string]$Directory) {
    if (-not (Test-Path -LiteralPath (Join-Path $Directory 'ffmpeg.exe'))) { return $false }
    if (-not (Test-Path -LiteralPath (Join-Path $Directory 'ffprobe.exe'))) { return $false }
    if (-not (Test-Path -LiteralPath (Join-Path $Directory 'metadata.json'))) { return $false }
    if (-not (Test-Path -LiteralPath (Join-Path $Directory 'SOURCE.txt'))) { return $false }
    foreach ($pattern in @('avcodec-*.dll', 'avformat-*.dll', 'avutil-*.dll', 'swresample-*.dll')) {
        if (-not (Get-ChildItem -LiteralPath $Directory -Filter $pattern -File -ErrorAction SilentlyContinue | Select-Object -First 1)) { return $false }
    }
    return $true
}

function Invoke-CheckedProcess([string]$FilePath, [string[]]$Arguments, [int]$TimeoutSeconds = 10) {
    $process = New-Object System.Diagnostics.Process
    $startInfo = New-Object System.Diagnostics.ProcessStartInfo
    $startInfo.FileName = $FilePath
    $startInfo.Arguments = ($Arguments -join ' ')
    $startInfo.UseShellExecute = $false
    $startInfo.CreateNoWindow = $true
    $startInfo.RedirectStandardOutput = $true
    $startInfo.RedirectStandardError = $true
    $process.StartInfo = $startInfo
    try {
        if (-not $process.Start()) { throw "Could not start process: $FilePath" }
        $stdoutTask = $process.StandardOutput.ReadToEndAsync()
        $stderrTask = $process.StandardError.ReadToEndAsync()
        if (-not $process.WaitForExit($TimeoutSeconds * 1000)) {
            $process.Kill()
            $process.WaitForExit()
            throw "Process timed out: $FilePath $($Arguments -join ' ')"
        }
        $output = (($stdoutTask.GetAwaiter().GetResult()) + ($stderrTask.GetAwaiter().GetResult())).Trim()
        $exitCode = $process.ExitCode
        if ($exitCode -ne 0) { throw "Process failed with exit code $exitCode`: $output" }
        return $output
    } finally {
        $process.Dispose()
    }
}

try {
    New-Item -ItemType Directory -Path $toolsDirectory, $downloadsDirectory -Force | Out-Null
    if (-not $Force -and (Test-SharedInstallation $toolsDirectory)) {
        $existingVersion = Invoke-CheckedProcess (Join-Path $toolsDirectory 'ffmpeg.exe') @('-version')
        Write-Host 'A complete FFmpeg Shared installation already exists. Use -Force to refresh it.' -ForegroundColor Yellow
        Write-Host ($existingVersion -split "`r?`n")[0] -ForegroundColor Green
        exit 0
    }

    Write-Host 'Querying the latest BtbN/FFmpeg-Builds GitHub release...' -ForegroundColor Cyan
    $release = Invoke-RestMethod -Uri $apiUrl -Headers $headers
    $eligible = @($release.assets | Where-Object {
        $_.name -match '^ffmpeg-.*win64-lgpl-shared.*\.zip$' -and
        $_.name -notmatch 'nonfree|win32|linux|macos|arm64'
    })
    $stable = @($eligible | Where-Object { $_.name -match '^ffmpeg-n\d' } | Sort-Object name -Descending)
    $asset = if ($stable.Count -gt 0) { $stable[0] } else { $eligible | Where-Object { $_.name -eq 'ffmpeg-master-latest-win64-lgpl-shared.zip' } | Select-Object -First 1 }
    if ($null -eq $asset) { throw 'No unambiguous Windows x64 LGPL Shared ZIP asset was found.' }

    $archivePath = Join-Path $downloadsDirectory $asset.name
    if (-not (Test-Path -LiteralPath $archivePath)) {
        Write-Host "Downloading $($asset.name)..." -ForegroundColor Cyan
        Invoke-WebRequest -Uri $asset.browser_download_url -Headers $headers -OutFile $archivePath -UseBasicParsing
    } else {
        Write-Host "Using existing download: $archivePath" -ForegroundColor Yellow
    }

    $sha256 = (Get-FileHash -LiteralPath $archivePath -Algorithm SHA256).Hash.ToLowerInvariant()
    $upstreamDigest = [string]$asset.digest
    $digestVerified = $false
    if (-not [string]::IsNullOrWhiteSpace($upstreamDigest)) {
        if ($upstreamDigest -notmatch '^sha256:([0-9a-fA-F]{64})$') { throw "Unsupported upstream digest format: $upstreamDigest" }
        if ($sha256 -ne $Matches[1].ToLowerInvariant()) { throw "SHA-256 mismatch. Local: $sha256 Upstream: $($Matches[1])" }
        $digestVerified = $true
        Write-Host 'SHA-256 matches the digest published by GitHub.' -ForegroundColor Green
    } else {
        Write-Warning 'The GitHub asset did not expose a digest. The local SHA-256 will be recorded without claiming upstream verification.'
    }

    if (Test-Path -LiteralPath $extractDirectory) { Remove-Item -LiteralPath $extractDirectory -Recurse -Force }
    New-Item -ItemType Directory -Path $extractDirectory -Force | Out-Null
    Expand-Archive -LiteralPath $archivePath -DestinationPath $extractDirectory -Force

    $ffmpegItem = Get-ChildItem -LiteralPath $extractDirectory -Recurse -File -Filter 'ffmpeg.exe' | Where-Object { $_.Directory.Name -eq 'bin' } | Select-Object -First 1
    if ($null -eq $ffmpegItem) { throw 'Could not find bin\ffmpeg.exe in the extracted archive.' }
    $binDirectory = $ffmpegItem.Directory.FullName
    if (-not (Test-Path -LiteralPath (Join-Path $binDirectory 'ffprobe.exe'))) { throw 'ffprobe.exe is missing from the selected bin directory.' }
    foreach ($pattern in @('avcodec-*.dll', 'avformat-*.dll', 'avutil-*.dll', 'swresample-*.dll')) {
        if (-not (Get-ChildItem -LiteralPath $binDirectory -Filter $pattern -File | Select-Object -First 1)) { throw "Shared build dependency is missing: $pattern" }
    }

    Get-ChildItem -LiteralPath $toolsDirectory -File | Where-Object { $_.Extension -in @('.exe', '.dll', '.pdb') } | Remove-Item -Force
    Get-ChildItem -LiteralPath $binDirectory -File | Where-Object { $_.Name -ne 'ffplay.exe' } | Copy-Item -Destination $toolsDirectory -Force

    $licensesDirectory = Join-Path $toolsDirectory 'licenses'
    New-Item -ItemType Directory -Path $licensesDirectory -Force | Out-Null
    $packageRoot = $ffmpegItem.Directory.Parent.FullName
    Get-ChildItem -LiteralPath $packageRoot -File | Where-Object { $_.Name -match '^(LICENSE|COPYING|README|RELEASE|BUILD)' } | Copy-Item -Destination $licensesDirectory -Force

    $installedFfmpeg = Join-Path $toolsDirectory 'ffmpeg.exe'
    $installedFfprobe = Join-Path $toolsDirectory 'ffprobe.exe'
    $ffmpegVersionOutput = Invoke-CheckedProcess $installedFfmpeg @('-version')
    $ffprobeVersionOutput = Invoke-CheckedProcess $installedFfprobe @('-version')
    $licenseOutput = Invoke-CheckedProcess $installedFfmpeg @('-L')
    $combined = "$ffmpegVersionOutput`n$licenseOutput"
    if ($combined -match '--enable-nonfree') { throw 'The downloaded FFmpeg build enables nonfree components.' }
    if ($combined -match '--enable-gpl') { throw 'The selected LGPL asset unexpectedly reports --enable-gpl.' }

    $ffmpegVersion = ($ffmpegVersionOutput -split "`r?`n")[0]
    $ffprobeVersion = ($ffprobeVersionOutput -split "`r?`n")[0]
    $metadata = [ordered]@{
        SourceRepository = 'https://github.com/BtbN/FFmpeg-Builds'
        ReleaseUrl = [string]$release.html_url
        AssetName = [string]$asset.name
        AssetUrl = [string]$asset.browser_download_url
        DownloadedAtUtc = [DateTime]::UtcNow.ToString('o')
        Sha256 = $sha256
        UpstreamDigest = if ([string]::IsNullOrWhiteSpace($upstreamDigest)) { $null } else { $upstreamDigest }
        UpstreamDigestVerified = $digestVerified
        FfmpegVersion = $ffmpegVersion
        FfprobeVersion = $ffprobeVersion
        LicenseVariant = 'LGPL Shared'
    }
    $metadata | ConvertTo-Json -Depth 4 | Set-Content -LiteralPath (Join-Path $toolsDirectory 'metadata.json') -Encoding UTF8
    @(
        'Source repository: https://github.com/BtbN/FFmpeg-Builds'
        "Release page: $($release.html_url)"
        "Asset: $($asset.name)"
        "Asset URL: $($asset.browser_download_url)"
        "Downloaded at UTC: $($metadata.DownloadedAtUtc)"
        "SHA-256: $sha256"
        "Upstream digest: $upstreamDigest"
        "Upstream digest verified: $digestVerified"
    ) | Set-Content -LiteralPath (Join-Path $toolsDirectory 'SOURCE.txt') -Encoding UTF8

    Write-Host $ffmpegVersion -ForegroundColor Green
    Write-Host $ffprobeVersion -ForegroundColor Green
    Write-Host "Installed to: $toolsDirectory" -ForegroundColor Green
} catch {
    Write-Error $_
    exit 1
} finally {
    if (Test-Path -LiteralPath $extractDirectory) { Remove-Item -LiteralPath $extractDirectory -Recurse -Force -ErrorAction SilentlyContinue }
}
