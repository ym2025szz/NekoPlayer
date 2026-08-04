$ErrorActionPreference = 'Stop'
$root = $PSScriptRoot
if (-not (Get-Command wsl.exe -ErrorAction SilentlyContinue)) { throw 'WSL2 is required to produce the verified Linux archive from Windows.' }
$escapedWindowsRoot = $root.Replace('\', '\\')
$linuxRoot = (& wsl.exe wslpath -a -- $escapedWindowsRoot).Trim()
if ([string]::IsNullOrWhiteSpace($linuxRoot)) { throw 'Could not translate the project path for WSL.' }
$linuxCommand = 'set -euo pipefail; chmod +x ./publish-linux-x64.sh ./packaging/linux/*.sh; ./publish-linux-x64.sh'
& wsl.exe --cd $linuxRoot bash -lc $linuxCommand
if ($LASTEXITCODE -ne 0) { throw "Linux publication failed with exit code $LASTEXITCODE" }
