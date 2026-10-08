[CmdletBinding()]
param([ValidateSet('Debug', 'Release')][string]$Configuration = 'Debug')

$ErrorActionPreference = 'Stop'
$root = [System.IO.Path]::GetFullPath($PSScriptRoot)
$nodeExecutable = Join-Path $root 'tools\node\node.exe'
$gatewayEntry = Join-Path $root 'tools\music-gateway\server.mjs'
if (-not (Test-Path -LiteralPath $nodeExecutable) -or -not (Test-Path -LiteralPath $gatewayEntry)) {
    Write-Warning 'The bundled online gateway is unavailable. Local music remains usable; run setup-music-gateway.ps1 to install the online runtime.'
}
dotnet run --project (Join-Path $root 'src\NekoPlayer.App\NekoPlayer.App.csproj') --configuration $Configuration
if ($LASTEXITCODE -ne 0) { exit $LASTEXITCODE }
