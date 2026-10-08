param(
    [string]$Version = '1.2.0',
    [switch]$Launch
)
$ErrorActionPreference = 'Stop'
$taskRoot = [IO.Path]::GetFullPath($PSScriptRoot).TrimEnd('\')
if ($Version -notmatch '^\d+\.\d+\.\d+$') { throw 'Only stable X.Y.Z releases can be installed.' }
Add-Type -AssemblyName System.IO.Compression.FileSystem

function Assert-WorkspacePath([string]$Path) {
    $resolved = [IO.Path]::GetFullPath($Path)
    if (-not $resolved.StartsWith($taskRoot + '\', [StringComparison]::OrdinalIgnoreCase)) {
        throw 'The installation path escaped the workspace.'
    }
    return $resolved
}
function Write-JsonFile([string]$Path, $Value) {
    [IO.File]::WriteAllText($Path, (ConvertTo-Json -InputObject $Value -Depth 12), [Text.UTF8Encoding]::new($false))
}
function Verify-Runtime([string]$Directory, [string]$ExpectedVersion) {
    $manifest = Get-Content -LiteralPath (Join-Path $Directory 'runtime-manifest.json') -Raw | ConvertFrom-Json
    if ($manifest.Version -ne $ExpectedVersion -or $manifest.Runtime -ne 'win-x64' -or -not $manifest.SelfContained -or @($manifest.Files).Count -eq 0) { throw 'The release runtime does not match the expected stable version.' }
    $prefix = [IO.Path]::GetFullPath($Directory).TrimEnd('\') + '\'
    $seen = [Collections.Generic.HashSet[string]]::new([StringComparer]::OrdinalIgnoreCase)
    foreach ($entry in $manifest.Files) {
        $file = [IO.Path]::GetFullPath((Join-Path $Directory $entry.Path))
        if (-not $file.StartsWith($prefix, [StringComparison]::OrdinalIgnoreCase) -or -not $seen.Add($file)) { throw 'A runtime manifest entry is duplicated or escaped the package.' }
        if (-not (Test-Path -LiteralPath $file -PathType Leaf) -or (Get-Item -LiteralPath $file).Length -ne $entry.Bytes -or (Get-FileHash -LiteralPath $file -Algorithm SHA256).Hash.ToLowerInvariant() -ne $entry.Sha256) { throw "Release integrity verification failed: $($entry.Path)" }
    }
    foreach ($file in Get-ChildItem -LiteralPath $Directory -File -Recurse -Force) {
        if (-not $seen.Contains($file.FullName) -and $file.FullName -notin @((Join-Path $Directory 'runtime-manifest.json'), (Join-Path $Directory 'installation.json'))) { throw "Unmanifested file in release runtime: $($file.Name)" }
    }
    $exeVersion = (Get-Item -LiteralPath (Join-Path $Directory 'NekoPlayer.exe')).VersionInfo
    if ($exeVersion.ProductVersion -ne $ExpectedVersion -or $exeVersion.FileVersion -ne "$ExpectedVersion.0") { throw 'The executable version does not match the stable package.' }
    Write-Output "Verified stable v$ExpectedVersion runtime: $($manifest.Files.Count) files."
}

function Wait-ForInstalledExit([string]$Executable) {
    $running = @(Get-Process -Name NekoPlayer -ErrorAction SilentlyContinue | Where-Object { $_.Path -eq $Executable })
    if ($running.Count -eq 0) { return }
    $productVersion = (Get-Item -LiteralPath $Executable).VersionInfo.ProductVersion
    # Older releases did not recognize this flag; it could launch a new GUI.
    if ($productVersion -notmatch '^\d+\.\d+\.\d+$' -or [Version]$productVersion -lt [Version]'1.2.0') { throw 'Close this older installed player window normally before installing its stable update.' }
    $profileOverride = [Environment]::GetEnvironmentVariable('NEKOPLAYER_DATA_ROOT', 'Process')
    $shutdown = $null
    try {
        [Environment]::SetEnvironmentVariable('NEKOPLAYER_DATA_ROOT', $null, 'Process')
        $deadline = [DateTime]::UtcNow.AddSeconds(30)
        $shutdown = Start-Process -FilePath $Executable -ArgumentList '--exit-for-update' -WorkingDirectory (Split-Path -Parent $Executable) -WindowStyle Hidden -PassThru
        $remaining = [Math]::Max(0, [int]($deadline - [DateTime]::UtcNow).TotalMilliseconds)
        if (-not $shutdown.WaitForExit($remaining)) { throw 'The update-exit helper did not finish within 30 seconds; the installed files were left unchanged.' }
        if ($shutdown.ExitCode -ne 0) { throw "The player could not finish shutdown (exit code $($shutdown.ExitCode)); the installed files were left unchanged." }
        foreach ($process in $running) {
            $remaining = [Math]::Max(0, [int]($deadline - [DateTime]::UtcNow).TotalMilliseconds)
            if (-not $process.WaitForExit($remaining)) { throw 'The installed player is still releasing resources; the installed files were left unchanged.' }
        }
        if (@(Get-Process -Name NekoPlayer -ErrorAction SilentlyContinue | Where-Object { $_.Path -eq $Executable }).Count -gt 0) { throw 'The installed player remains open; the installed files were left unchanged.' }
    } finally {
        [Environment]::SetEnvironmentVariable('NEKOPLAYER_DATA_ROOT', $profileOverride, 'Process')
        foreach ($process in $running) { $process.Dispose() }
        if ($null -ne $shutdown) { $shutdown.Dispose() }
    }
}

$releaseRoot = Assert-WorkspacePath (Join-Path $taskRoot 'releases')
$installDirectory = Assert-WorkspacePath (Join-Path $taskRoot 'artifacts\publish\win-x64')
$targetExecutable = Join-Path $installDirectory 'NekoPlayer.exe'
$archive = Assert-WorkspacePath (Join-Path $taskRoot "artifacts\NekoPlayer-v$Version-win-x64.zip")
$checksumFile = $archive + '.sha256'
if (-not (Test-Path -LiteralPath $archive -PathType Leaf) -or -not (Test-Path -LiteralPath $checksumFile -PathType Leaf)) { throw 'The stable release archive or checksum is missing.' }
$archiveHash = (Get-FileHash -LiteralPath $archive -Algorithm SHA256).Hash.ToLowerInvariant()
$expectedHash = (Get-Content -LiteralPath $checksumFile -Raw).Trim().Split(' ')[0].ToLowerInvariant()
if ($expectedHash -notmatch '^[a-f0-9]{64}$') { throw 'The stable release checksum is not a SHA256 value.' }
if ($archiveHash -ne $expectedHash) { throw 'The stable release archive checksum does not match.' }

# Release archives are immutable; existing versions must have exactly the same contents.
function Save-StableArchives {
New-Item -ItemType Directory -Path $releaseRoot -Force | Out-Null
$releaseEntries = @()
foreach ($sourceArchive in @(Get-Item -LiteralPath $archive)) {
    if ($sourceArchive.Name -notmatch '^NekoPlayer-v(\d+\.\d+\.\d+)-win-x64\.zip$') { continue }
    $releaseVersion = $Matches[1]
    $releaseDirectory = Assert-WorkspacePath (Join-Path $releaseRoot "v$releaseVersion")
    New-Item -ItemType Directory -Path $releaseDirectory -Force | Out-Null
    $savedArchive = Join-Path $releaseDirectory $sourceArchive.Name
    $hash = (Get-FileHash -LiteralPath $sourceArchive.FullName -Algorithm SHA256).Hash.ToLowerInvariant()
    if (Test-Path -LiteralPath $savedArchive) {
        if ((Get-FileHash -LiteralPath $savedArchive -Algorithm SHA256).Hash.ToLowerInvariant() -ne $hash) { throw "Archived stable v$releaseVersion is immutable and differs from the supplied archive." }
    } else { Copy-Item -LiteralPath $sourceArchive.FullName -Destination $savedArchive }
    if (Test-Path -LiteralPath ($savedArchive + '.sha256')) {
        $savedHash = (Get-Content -LiteralPath ($savedArchive + '.sha256') -Raw).Trim().Split(' ')[0].ToLowerInvariant()
        if ($savedHash -ne $hash) { throw 'The archived stable checksum is immutable and differs.' }
    } else { [IO.File]::WriteAllText($savedArchive + '.sha256', "$hash  $($sourceArchive.Name)`r`n", [Text.UTF8Encoding]::new($false)) }
    foreach ($document in @("RELEASE-NOTES-v$releaseVersion.md", "MANUAL-ACCEPTANCE-v$releaseVersion.md")) {
        $notes = Join-Path (Join-Path $stagedRuntime 'docs') $document
        $savedNotes = Join-Path $releaseDirectory $document
        if ((Test-Path -LiteralPath $notes) -and -not (Test-Path -LiteralPath $savedNotes)) { Copy-Item -LiteralPath $notes -Destination $savedNotes }
    }
    $releaseMetadata = [ordered]@{ Version = $releaseVersion; Channel = 'stable'; Runtime = 'win-x64'; Archive = $sourceArchive.Name; Bytes = $sourceArchive.Length; Sha256 = $hash }
    $metadataPath = Join-Path $releaseDirectory 'release.json'
    if (Test-Path -LiteralPath $metadataPath) {
        $existing = Get-Content -LiteralPath $metadataPath -Raw | ConvertFrom-Json
        foreach ($key in $releaseMetadata.Keys) { if ($existing.$key -ne $releaseMetadata[$key]) { throw "Archived stable release metadata differs: $key" } }
    } else { Write-JsonFile $metadataPath $releaseMetadata }
}
foreach ($directory in Get-ChildItem -LiteralPath $releaseRoot -Directory) {
    if ($directory.Name -notmatch '^v(\d+\.\d+\.\d+)$') { continue }
    $releaseVersion = $Matches[1]
    $metadata = Get-Content -LiteralPath (Join-Path $directory.FullName 'release.json') -Raw | ConvertFrom-Json
    $expectedName = "NekoPlayer-v$releaseVersion-win-x64.zip"
    $savedArchive = Assert-WorkspacePath (Join-Path $directory.FullName $expectedName)
    $savedHash = (Get-Content -LiteralPath ($savedArchive + '.sha256') -Raw).Trim().Split(' ')[0].ToLowerInvariant()
    if ($metadata.Version -ne $releaseVersion -or $metadata.Channel -ne 'stable' -or $metadata.Runtime -ne 'win-x64' -or $metadata.Archive -ne $expectedName -or
        (Get-Item -LiteralPath $savedArchive).Length -ne $metadata.Bytes -or $savedHash -ne $metadata.Sha256 -or
        (Get-FileHash -LiteralPath $savedArchive -Algorithm SHA256).Hash.ToLowerInvariant() -ne $metadata.Sha256) { throw "Archived stable v$releaseVersion integrity verification failed." }
    $readme = Join-Path $directory.FullName 'README.md'
    if (-not (Test-Path -LiteralPath $readme)) {
        $readmeText = "# NekoPlayer v$releaseVersion 正式版`r`n`r`n平台：Windows x64；自包含运行时。`r`n`r`n安装包：$expectedName`r`n`r`nSHA256：$($metadata.Sha256)`r`n`r`n本目录的正式版 ZIP、校验值与发布记录不可替换；修订必须使用新版本号。`r`n"
        [IO.File]::WriteAllText($readme, $readmeText, [Text.UTF8Encoding]::new($false))
    }
    $releaseEntries += $metadata
}
return @($releaseEntries | Sort-Object { [Version]$_.Version })
}

$stageDirectory = Assert-WorkspacePath (Join-Path $taskRoot ('artifacts\install-staging\' + [Guid]::NewGuid().ToString('N')))
New-Item -ItemType Directory -Path $stageDirectory -Force | Out-Null
$zip = [IO.Compression.ZipFile]::OpenRead($archive)
try {
    $zipTargets = [Collections.Generic.HashSet[string]]::new([StringComparer]::OrdinalIgnoreCase)
    foreach ($entry in $zip.Entries) {
        $extractionTarget = [IO.Path]::GetFullPath((Join-Path $stageDirectory $entry.FullName))
        if (-not $extractionTarget.StartsWith($stageDirectory + '\', [StringComparison]::OrdinalIgnoreCase) -or -not $zipTargets.Add($extractionTarget)) { throw 'An archive entry is duplicated or escaped the staging directory.' }
        if (($entry.ExternalAttributes -shr 16 -band 0xF000) -eq 0xA000) { throw 'Release archives must not contain symbolic links.' }
    }
} finally { $zip.Dispose() }
Expand-Archive -LiteralPath $archive -DestinationPath $stageDirectory
$manifestFiles = @(Get-ChildItem -LiteralPath $stageDirectory -Filter 'runtime-manifest.json' -Recurse -File)
if ($manifestFiles.Count -ne 1) { throw 'The archive must contain exactly one runtime manifest.' }
$stagedRuntime = Assert-WorkspacePath $manifestFiles[0].DirectoryName
Verify-Runtime $stagedRuntime $Version
$releaseEntries = @(Save-StableArchives)

Wait-ForInstalledExit $targetExecutable
$installedManifest = Join-Path $installDirectory 'runtime-manifest.json'
$previousVersion = if (Test-Path -LiteralPath $installedManifest) { (Get-Content -LiteralPath $installedManifest -Raw | ConvertFrom-Json).Version } else { 'unknown' }
$backupRoot = Assert-WorkspacePath (Join-Path $taskRoot 'artifacts\installation-backups')
New-Item -ItemType Directory -Path $backupRoot -Force | Out-Null
New-Item -ItemType Directory -Path (Assert-WorkspacePath (Split-Path -Parent $installDirectory)) -Force | Out-Null
$backupDirectory = Assert-WorkspacePath (Join-Path $backupRoot ("v$previousVersion-" + [DateTime]::UtcNow.ToString('yyyyMMdd-HHmmss') + '-' + [Guid]::NewGuid().ToString('N').Substring(0,8)))
$previousMoved = $false
$newMoved = $false
$releaseRecordBackups = @(foreach ($name in @('installed.json', 'index.json')) {
    $recordPath = Assert-WorkspacePath (Join-Path $releaseRoot $name)
    $existed = Test-Path -LiteralPath $recordPath -PathType Leaf
    [pscustomobject]@{ Path = $recordPath; Existed = $existed; Bytes = if ($existed) { [IO.File]::ReadAllBytes($recordPath) } else { $null } }
})
try {
    if (@(Get-Process -Name NekoPlayer -ErrorAction SilentlyContinue | Where-Object { $_.Path -eq $targetExecutable }).Count -gt 0) { throw 'The installed player was reopened; close it before installing.' }
    if (Test-Path -LiteralPath $installDirectory) { Move-Item -LiteralPath $installDirectory -Destination $backupDirectory; $previousMoved = $true }
    Move-Item -LiteralPath $stagedRuntime -Destination $installDirectory
    $newMoved = $true
    Verify-Runtime $installDirectory $Version
    $installInfo = [ordered]@{ Version = $Version; Channel = 'stable'; InstalledAtUtc = [DateTimeOffset]::UtcNow.ToString('O'); PackageSha256 = $archiveHash; PreviousVersion = $previousVersion; PreviousInstallationBackup = $backupDirectory; ReleaseArchive = "releases/v$Version/" + [IO.Path]::GetFileName($archive) }
    Write-JsonFile (Join-Path $installDirectory 'installation.json') $installInfo
    Write-JsonFile (Join-Path $releaseRoot 'installed.json') $installInfo
    Write-JsonFile (Join-Path $releaseRoot 'index.json') ([ordered]@{ Channel = 'stable'; CurrentVersion = $Version; Releases = $releaseEntries })
} catch {
    # Retain a failed update for diagnosis, then restore the original installation.
    if ($newMoved -and (Test-Path -LiteralPath $installDirectory)) {
        # A flat ZIP moves the staging root itself; recreate its parent before
        # retaining the failed runtime so rollback works for either ZIP shape.
        New-Item -ItemType Directory -Path (Assert-WorkspacePath $stageDirectory) -Force | Out-Null
        Move-Item -LiteralPath $installDirectory -Destination (Assert-WorkspacePath (Join-Path $stageDirectory 'failed-installation'))
    }
    if ($previousMoved -and (Test-Path -LiteralPath $backupDirectory)) { Move-Item -LiteralPath $backupDirectory -Destination $installDirectory }
    foreach ($record in $releaseRecordBackups) {
        if ($record.Existed) { [IO.File]::WriteAllBytes($record.Path, $record.Bytes) }
        elseif (Test-Path -LiteralPath $record.Path) { Remove-Item -LiteralPath $record.Path -Force }
    }
    throw
}
if (Test-Path -LiteralPath $stageDirectory) { Remove-Item -LiteralPath (Assert-WorkspacePath $stageDirectory) -Recurse -Force }
[IO.File]::WriteAllText((Join-Path $taskRoot '启动猫娘播放器.cmd'), "@echo off`r`nstart `"`" `"%~dp0artifacts\publish\win-x64\NekoPlayer.exe`"`r`n", [Text.Encoding]::ASCII)
Write-Output "Installed stable v$Version at $installDirectory"
Write-Output "Previous installation preserved at $backupDirectory"
if ($Launch) {
    # The installed application uses the user's normal profile, never a validation profile.
    Remove-Item Env:NEKOPLAYER_DATA_ROOT -ErrorAction SilentlyContinue
    $player = Start-Process -FilePath $targetExecutable -WorkingDirectory $installDirectory -WindowStyle Hidden -PassThru
    Write-Output "Started installed stable release. PID=$($player.Id)"
}
