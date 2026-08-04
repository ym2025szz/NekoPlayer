$ErrorActionPreference = 'Stop'
$root = $PSScriptRoot
dotnet run --project (Join-Path $root 'src\NekoPlayer.App\NekoPlayer.App.csproj')
