$ErrorActionPreference = 'Stop'
$projectRoot = [IO.Path]::GetFullPath((Join-Path $PSScriptRoot '..')).TrimEnd('\')
$verificationRoot = Join-Path $projectRoot ('artifacts\release-script-verification\' + [Guid]::NewGuid().ToString('N'))
if (-not ([IO.Path]::GetFullPath($verificationRoot)).StartsWith($projectRoot + '\artifacts\release-script-verification\', [StringComparison]::OrdinalIgnoreCase)) { throw 'Verification path escaped its isolated artifacts directory.' }
New-Item -ItemType Directory -Path $verificationRoot -Force | Out-Null
$sourceExe = Join-Path $projectRoot 'artifacts\publish\win-x64\NekoPlayer.exe'
$fixtureVersion = (Get-Item -LiteralPath $sourceExe).VersionInfo.ProductVersion
if ($fixtureVersion -notmatch '^\d+\.\d+\.\d+$') { throw 'A stable executable is required as a read-only version-resource fixture.' }
$fixtureRuntime = Join-Path $verificationRoot 'fixture-runtime'
New-Item -ItemType Directory -Path $fixtureRuntime -Force | Out-Null
Copy-Item -LiteralPath $sourceExe -Destination (Join-Path $fixtureRuntime 'NekoPlayer.exe')
[IO.File]::WriteAllText((Join-Path $fixtureRuntime 'README.md'), 'Isolated installer verification fixture; never launched.')
$fixtureFiles = @(Get-ChildItem -LiteralPath $fixtureRuntime -File | ForEach-Object { [ordered]@{ Path = $_.Name; Bytes = $_.Length; Sha256 = (Get-FileHash -LiteralPath $_.FullName -Algorithm SHA256).Hash.ToLowerInvariant() } })
$manifest = [ordered]@{ SchemaVersion = 1; Version = $fixtureVersion; Runtime = 'win-x64'; SelfContained = $true; Files = $fixtureFiles }
[IO.File]::WriteAllText((Join-Path $fixtureRuntime 'runtime-manifest.json'), ($manifest | ConvertTo-Json -Depth 6))
Copy-Item -LiteralPath (Join-Path $projectRoot 'install-release.ps1') -Destination $verificationRoot
$installer = Join-Path $verificationRoot 'install-release.ps1'
$testArtifacts = Join-Path $verificationRoot 'artifacts'
New-Item -ItemType Directory -Path $testArtifacts -Force | Out-Null
$testArchive = Join-Path $testArtifacts "NekoPlayer-v$fixtureVersion-win-x64.zip"
Add-Type -AssemblyName System.IO.Compression.FileSystem
[IO.Compression.ZipFile]::CreateFromDirectory($fixtureRuntime, $testArchive)
$archiveHash = (Get-FileHash -LiteralPath $testArchive -Algorithm SHA256).Hash.ToLowerInvariant()
[IO.File]::WriteAllText($testArchive + '.sha256', "$archiveHash  $([IO.Path]::GetFileName($testArchive))`r`n")
$originalArchive = [IO.File]::ReadAllBytes($testArchive)
$checks = [Collections.Generic.List[string]]::new()
function Assert-Test([bool]$Passed, [string]$Name) { if (-not $Passed) { throw "FAIL: $Name" }; $checks.Add($Name); Write-Output "PASS: $Name" }
function Assert-Rejected([scriptblock]$Action, [string]$Expected, [string]$Name) {
    $message = ''
    try { & $Action | Out-Null } catch { $message = $_.Exception.Message }
    if ($message -notlike "*$Expected*") { Write-Output "Unexpected rejection for $Name : $message" }
    Assert-Test ($message -like "*$Expected*") $Name
}

try {
    & $installer -Version $fixtureVersion | Out-Null
    $testInstalled = Join-Path $testArtifacts 'publish\win-x64'
    $testInstalledExe = Join-Path $testInstalled 'NekoPlayer.exe'
    $installedExeHash = (Get-FileHash -LiteralPath $testInstalledExe -Algorithm SHA256).Hash
    $releaseDirectory = Join-Path $verificationRoot "releases\v$fixtureVersion"
    $savedArchive = Join-Path $releaseDirectory ([IO.Path]::GetFileName($testArchive))
    Assert-Test ((Test-Path -LiteralPath (Join-Path $releaseDirectory 'README.md')) -and
        (Get-FileHash -LiteralPath $savedArchive -Algorithm SHA256).Hash.ToLowerInvariant() -eq $archiveHash -and
        (Get-Content -LiteralPath (Join-Path $verificationRoot 'releases\index.json') -Raw | ConvertFrom-Json).CurrentVersion -eq $fixtureVersion) 'isolated install, archive SHA256, release README and current-version index'

    [IO.File]::WriteAllText($testArchive + '.sha256', ('0' * 64))
    Assert-Rejected { & $installer -Version $fixtureVersion } 'checksum does not match' 'checksum failure leaves the previous installation intact'
    [IO.File]::WriteAllText($testArchive + '.sha256', "$archiveHash  $([IO.Path]::GetFileName($testArchive))`r`n")

    # ZIP permits trailing bytes, so runtime validation still passes; stable archive
    # immutability must independently reject a newly checksummed replacement.
    $changedArchive = [byte[]]::new($originalArchive.Length + 1)
    [Array]::Copy($originalArchive, $changedArchive, $originalArchive.Length)
    [IO.File]::WriteAllBytes($testArchive, $changedArchive)
    $changedHash = (Get-FileHash -LiteralPath $testArchive -Algorithm SHA256).Hash.ToLowerInvariant()
    [IO.File]::WriteAllText($testArchive + '.sha256', "$changedHash  $([IO.Path]::GetFileName($testArchive))`r`n")
    Assert-Rejected { & $installer -Version $fixtureVersion } 'immutable and differs' 'the same stable version cannot replace its archived package'
    [IO.File]::WriteAllBytes($testArchive, $originalArchive)
    [IO.File]::WriteAllText($testArchive + '.sha256', "$archiveHash  $([IO.Path]::GetFileName($testArchive))`r`n")

    if ([Version]$fixtureVersion -lt [Version]'1.2.0') {
        # Only the process-list boundary is faked. Real version resources must
        # prevent the installer from ever invoking an unsupported old CLI flag.
        function Get-Process { [CmdletBinding()] param([string]$Name) if ($Name -eq 'NekoPlayer') { [pscustomobject]@{ Path = $testInstalledExe } } }
        function Start-Process { [CmdletBinding()] param([string]$FilePath, $ArgumentList, $WorkingDirectory, $WindowStyle, [switch]$PassThru) throw 'A legacy executable was incorrectly invoked.' }
        Assert-Rejected { & $installer -Version $fixtureVersion } 'older installed player window normally' 'a running v1.1.1 uses normal window close, never an unknown update flag'
        Remove-Item Function:Get-Process
        Remove-Item Function:Start-Process
    }

    # Force only the post-move verification to fail; real filesystem moves must
    # restore the original installation from its backup.
    function Get-FileHash {
        [CmdletBinding()] param([string]$LiteralPath, [string]$Algorithm)
        if ($LiteralPath -eq $testInstalledExe) { return [pscustomobject]@{ Hash = ('0' * 64) } }
        Microsoft.PowerShell.Utility\Get-FileHash -LiteralPath $LiteralPath -Algorithm $Algorithm
    }
    Assert-Rejected { & $installer -Version $fixtureVersion } 'Release integrity verification failed' 'post-move integrity failure rolls the original installation back'
    Remove-Item Function:Get-FileHash
    Assert-Test ((Get-FileHash -LiteralPath $testInstalledExe -Algorithm SHA256).Hash -eq $installedExeHash -and
        (Get-FileHash -LiteralPath $savedArchive -Algorithm SHA256).Hash.ToLowerInvariant() -eq $archiveHash) 'rollback preserves the prior executable and immutable release archive'

    $report = [ordered]@{ Passed = $true; ExecutedAtUtc = [DateTimeOffset]::UtcNow.ToString('O'); FixtureVersion = $fixtureVersion; FixtureWasLaunched = $false; FormalInstallationModified = $false; Checks = @($checks) }
    $reportPath = Join-Path $projectRoot 'artifacts\release-script-verification.json'
    [IO.File]::WriteAllText($reportPath, ($report | ConvertTo-Json -Depth 6))
    Write-Output "Report: $reportPath"
} finally {
    foreach ($name in @('Get-Process', 'Start-Process', 'Get-FileHash')) { Remove-Item -LiteralPath ('Function:' + $name) -ErrorAction SilentlyContinue }
    $resolvedVerification = [IO.Path]::GetFullPath($verificationRoot)
    if (-not $resolvedVerification.StartsWith($projectRoot + '\artifacts\release-script-verification\', [StringComparison]::OrdinalIgnoreCase)) { throw 'Refused to clean an unexpected verification path.' }
    if (Test-Path -LiteralPath $resolvedVerification) { Remove-Item -LiteralPath $resolvedVerification -Recurse -Force }
}
