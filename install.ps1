# Lofi Desk installer: builds if needed, creates settings.ini, starts the live wallpaper with Windows,
# registers the screensaver and makes the primary monitor's static wallpaper pure black.
#   powershell -ExecutionPolicy Bypass -File install.ps1
#   powershell -ExecutionPolicy Bypass -File install.ps1 -NoScreensaver -NoBlackWallpaper
param([switch]$NoScreensaver, [switch]$NoBlackWallpaper)
$ErrorActionPreference = 'Stop'
Set-Location $PSScriptRoot
$dir = $PSScriptRoot

# 1. binaries (a release zip already has them; a git clone builds them)
if (-not (Test-Path "$dir\LofiDesk.exe")) {
    & powershell -NoProfile -ExecutionPolicy Bypass -File "$dir\build.ps1"
    if ($LASTEXITCODE -ne 0) { throw 'build failed' }
    Copy-Item "$dir\build\LofiDesk.new.exe" "$dir\LofiDesk.exe" -Force
}
Copy-Item "$dir\LofiDesk.exe" "$dir\LofiDesk.scr" -Force

# 2. settings
if (-not (Test-Path "$dir\settings.ini")) { Copy-Item "$dir\settings.example.ini" "$dir\settings.ini"; Write-Host 'Created settings.ini - edit it to set your city, language and scene.' }

# 3. WebView2 runtime (preinstalled on Windows 11; Windows 10 may need it)
$wv = Get-ItemProperty 'HKLM:\SOFTWARE\WOW6432Node\Microsoft\EdgeUpdate\Clients\{F3017226-FE2A-4295-8BDF-00C3A9A7E4C5}' -ErrorAction SilentlyContinue
if (-not $wv) { Write-Warning 'WebView2 Runtime not found. Install it from https://developer.microsoft.com/microsoft-edge/webview2/ and run install.ps1 again.' }

# 4. start with Windows
$lnk = Join-Path ([Environment]::GetFolderPath('Startup')) 'Lofi Desk wallpaper.lnk'
$sh = New-Object -ComObject WScript.Shell
$s = $sh.CreateShortcut($lnk); $s.TargetPath = "$dir\LofiDesk.exe"; $s.Arguments = '/w'; $s.WorkingDirectory = $dir; $s.Save()
Write-Host "Autostart: $lnk"

# 5. screensaver: primary monitor black, the others show the scene
if (-not $NoScreensaver) {
    $k = 'HKCU:\Control Panel\Desktop'
    Set-ItemProperty $k SCRNSAVE.EXE "$dir\LofiDesk.scr"
    Set-ItemProperty $k ScreenSaveActive '1'
    if (-not (Get-ItemProperty $k -Name ScreenSaveTimeOut -ErrorAction SilentlyContinue)) { Set-ItemProperty $k ScreenSaveTimeOut '300' }
    Write-Host 'Screensaver: Lofi Desk (change the timeout in Settings > Personalization > Lock screen > Screen saver)'
}

# 6. static wallpaper under the live one: black on the primary monitor (good for OLED)
if (-not $NoBlackWallpaper) { & powershell -NoProfile -ExecutionPolicy Bypass -File "$dir\set-wallpapers.ps1" | Out-Null }

# 7. go
Get-Process | Where-Object { $_.Path -like "$dir\*" } | Stop-Process -Force -ErrorAction SilentlyContinue
Start-Process "$dir\LofiDesk.exe" '/w' -WorkingDirectory $dir
Write-Host 'Lofi Desk is running. Right-click the mascot icon in the tray for options.'
