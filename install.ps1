# Builds a ReadyToRun single-file exe and installs it to %LOCALAPPDATA%\Programs\LiteMixer, then starts it.
$ErrorActionPreference = "Stop"
$target = Join-Path $env:LOCALAPPDATA "Programs\LiteMixer"

$running = Get-Process LiteMixer -ErrorAction SilentlyContinue
$running | Stop-Process -Force
$running | Wait-Process -Timeout 10 -ErrorAction SilentlyContinue
Start-Sleep -Milliseconds 300

dotnet publish "$PSScriptRoot\LiteMixer.csproj" -c Release -o $target
if ($LASTEXITCODE -ne 0) { throw "publish failed" }

# Start menu shortcut
$shell = New-Object -ComObject WScript.Shell
$lnk = $shell.CreateShortcut((Join-Path ([Environment]::GetFolderPath("Programs")) "LiteMixer.lnk"))
$lnk.TargetPath = Join-Path $target "LiteMixer.exe"
$lnk.WorkingDirectory = $target
$lnk.Save()

Start-Process (Join-Path $target "LiteMixer.exe")
Write-Output "Installed to $target"
