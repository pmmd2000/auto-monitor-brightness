# Builds Auto Monitor Brightness and installs it for the current user (no admin needed).
#   - publishes a single small exe to %LOCALAPPDATA%\Programs\AutoMonitorBrightness
#   - registers it to start at sign-in (HKCU Run key, done by the app on first launch)
#   - starts it
# Re-run any time to update. Use -Uninstall to remove.
param([switch]$Uninstall)

$ErrorActionPreference = 'Stop'
$target = Join-Path $env:LOCALAPPDATA 'Programs\AutoMonitorBrightness'
$exe = Join-Path $target 'AutoMonitorBrightness.exe'

Get-Process AutoMonitorBrightness -ErrorAction SilentlyContinue | Stop-Process -Force
Start-Sleep -Milliseconds 500

if ($Uninstall) {
    Remove-ItemProperty -Path 'HKCU:\Software\Microsoft\Windows\CurrentVersion\Run' -Name 'AutoMonitorBrightness' -ErrorAction SilentlyContinue
    if (Test-Path $target) { Remove-Item $target -Recurse -Force }
    Write-Host "Auto Monitor Brightness removed. Settings and log are kept in $env:APPDATA\AutoMonitorBrightness"
    return
}

dotnet publish "$PSScriptRoot\AutoMonitorBrightness.csproj" -c Release -r win-x64 --self-contained false `
    -p:PublishSingleFile=true -p:DebugType=none -o $target -nologo
if ($LASTEXITCODE -ne 0) { throw 'Build failed' }

# Point an existing autostart entry at the installed copy (first launch creates it otherwise).
$run = 'HKCU:\Software\Microsoft\Windows\CurrentVersion\Run'
if (Get-ItemProperty -Path $run -Name 'AutoMonitorBrightness' -ErrorAction SilentlyContinue) {
    Set-ItemProperty -Path $run -Name 'AutoMonitorBrightness' -Value "`"$exe`" --autostart"
}

Start-Process $exe
Write-Host "Installed to $exe and started. Look for the sun icon in the tray."
