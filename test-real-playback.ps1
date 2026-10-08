param(
    [string]$AudioDirectory,
    [ValidateRange(0.0, 1.0)]
    [double]$Volume = 0.15
)

$ErrorActionPreference = 'Stop'
$root = $PSScriptRoot
if ([string]::IsNullOrWhiteSpace($AudioDirectory)) {
    $projectFolderName = -join ([char]0x9879, [char]0x76EE)
    $audioFolderName = -join ([char]0x6D4B, [char]0x8BD5, [char]0x97F3, [char]0x9891)
    $AudioDirectory = Join-Path (Join-Path (Join-Path $env:USERPROFILE 'Desktop') $projectFolderName) $audioFolderName
}
$ffmpegDirectory = Join-Path $root 'tools\ffmpeg'
$verifierProject = Join-Path $root 'tools\NekoPlayer.PlaybackVerifier\NekoPlayer.PlaybackVerifier.csproj'
$reportsDirectory = Join-Path $root 'artifacts\test-reports'
$logPath = Join-Path $reportsDirectory 'playback-verification.log'

function Test-FfmpegRuntime([string]$Directory) {
    if (-not (Test-Path -LiteralPath (Join-Path $Directory 'ffmpeg.exe'))) { return $false }
    if (-not (Test-Path -LiteralPath (Join-Path $Directory 'ffprobe.exe'))) { return $false }
    foreach ($pattern in @('avcodec-*.dll', 'avformat-*.dll', 'avutil-*.dll', 'swresample-*.dll')) {
        if (-not (Get-ChildItem -LiteralPath $Directory -Filter $pattern -File -ErrorAction SilentlyContinue | Select-Object -First 1)) { return $false }
    }
    return $true
}

try {
    New-Item -ItemType Directory -Path $reportsDirectory -Force | Out-Null
    if (-not (Test-FfmpegRuntime $ffmpegDirectory)) { throw 'FFmpeg Shared runtime is incomplete. Run setup-ffmpeg.ps1 first.' }
    if (-not (Test-Path -LiteralPath $AudioDirectory)) { throw "Audio directory does not exist: $AudioDirectory" }
    $mp3Files = @(Get-ChildItem -LiteralPath $AudioDirectory -File -Filter '*.mp3' | Sort-Object Name)
    if ($mp3Files.Count -lt 2) { Write-Warning "At least two MP3 files are required. Found: $($mp3Files.Count). The verifier will record this precheck failure." }

    dotnet build $verifierProject -c Release
    if ($LASTEXITCODE -ne 0) { throw "PlaybackVerifier build failed with exit code $LASTEXITCODE" }

    $previousErrorActionPreference = $ErrorActionPreference
    $ErrorActionPreference = 'Continue'
    $output = & dotnet run --project $verifierProject -c Release --no-build -- --audio-directory $AudioDirectory --volume $Volume 2>&1
    $exitCode = $LASTEXITCODE
    $ErrorActionPreference = $previousErrorActionPreference
    $output | ForEach-Object { Write-Host $_ }
    $output | Out-File -LiteralPath $logPath -Encoding utf8
    if ($exitCode -ne 0) { throw "PlaybackVerifier failed with exit code $exitCode" }
    Write-Host 'Technical playback verification passed.' -ForegroundColor Green
    Write-Host 'Manual listening acceptance is still required.' -ForegroundColor Yellow
    exit 0
} catch {
    $errorText = $_ | Out-String
    Write-Host $errorText -ForegroundColor Red
    $errorText | Add-Content -LiteralPath $logPath -Encoding utf8
    exit 1
}
