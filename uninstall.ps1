# Lofi Desk uninstaller: stops it, removes autostart and the screensaver registration.
# Your files (settings.ini, notes, art) stay in this folder — delete the folder to remove everything.
$dir = $PSScriptRoot
Get-Process | Where-Object { $_.Path -like "$dir\*" } | Stop-Process -Force -ErrorAction SilentlyContinue
Remove-Item (Join-Path ([Environment]::GetFolderPath('Startup')) 'Lofi Desk wallpaper.lnk') -ErrorAction SilentlyContinue
$k = 'HKCU:\Control Panel\Desktop'
if ((Get-ItemProperty $k -Name SCRNSAVE.EXE -ErrorAction SilentlyContinue).'SCRNSAVE.EXE' -like "$dir\*") {
    Remove-ItemProperty $k SCRNSAVE.EXE
    Set-ItemProperty $k ScreenSaveActive '0'
}
Remove-Item "$env:LOCALAPPDATA\LofiDesk" -Recurse -Force -ErrorAction SilentlyContinue   # WebView2 cache, logs
Write-Host 'Lofi Desk removed. Pick a new wallpaper in Settings > Personalization.'
