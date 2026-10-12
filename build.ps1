# Lofi Desk build script.
#   powershell -ExecutionPolicy Bypass -File build.ps1            -> build\LofiDesk.new.exe + scene.en.html
#   powershell -ExecutionPolicy Bypass -File build.ps1 -Install   -> also copies it to LofiDesk.exe / LofiDesk.scr
# Needs only what ships with Windows: the .NET Framework 4.x C# compiler. The WebView2 SDK is downloaded once from NuGet.
param([switch]$Install)
$ErrorActionPreference = 'Stop'
Set-Location $PSScriptRoot
New-Item -ItemType Directory build -Force | Out-Null
$csc = 'C:\Windows\Microsoft.NET\Framework64\v4.0.30319\csc.exe'
if (-not (Test-Path $csc)) { throw 'C# compiler not found (.NET Framework 4.x is required)' }

# 1. WebView2 SDK (Microsoft.Web.WebView2 from NuGet): two managed DLLs + the native loader
$wv = '1.0.4258.31'
if (-not (Test-Path Microsoft.Web.WebView2.Core.dll)) {
    Write-Host "Downloading WebView2 SDK $wv from NuGet..."
    $pkg = "build\webview2.$wv.zip"
    Invoke-WebRequest "https://www.nuget.org/api/v2/package/Microsoft.Web.WebView2/$wv" -OutFile $pkg -UseBasicParsing
    Expand-Archive $pkg build\wv2pkg -Force
    Copy-Item build\wv2pkg\lib\net462\Microsoft.Web.WebView2.Core.dll, build\wv2pkg\lib\net462\Microsoft.Web.WebView2.WinForms.dll .
    Copy-Item build\wv2pkg\runtimes\win-x64\native\WebView2Loader.dll .
}

# 2. the host
$wm = 'C:\Windows\System32\WinMetadata'
$rt = Get-ChildItem 'C:\Windows\Microsoft.NET\assembly\GAC_MSIL\System.Runtime' -Recurse -Filter System.Runtime.dll | Select-Object -First 1 -ExpandProperty FullName
& $csc /nologo /target:winexe /platform:x64 /optimize+ /out:build\LofiDesk.new.exe `
  /r:System.Windows.Forms.dll /r:System.Drawing.dll /r:System.Web.Extensions.dll "/r:$rt" `
  "/r:$wm\Windows.Media.winmd" "/r:$wm\Windows.Foundation.winmd" "/r:$wm\Windows.Storage.winmd" `
  /r:Microsoft.Web.WebView2.Core.dll /r:Microsoft.Web.WebView2.WinForms.dll `
  LofiSaver.cs Widgets.cs AppUsage.cs
if ($LASTEXITCODE -ne 0) { exit $LASTEXITCODE }

# 3. translations: scene.html is the Russian original, scene.en.html is generated from i18n/en.json
& $csc /nologo /out:build\i18n.exe /r:System.Web.Extensions.dll tools\i18n.cs
if ($LASTEXITCODE -ne 0) { exit $LASTEXITCODE }
& .\build\i18n.exe apply scene.html i18n\en.json scene.en.html
if ($LASTEXITCODE -ne 0) { Write-Warning 'some strings have no English translation yet (see above) - run: build\i18n.exe extract scene.html i18n\strings.json' }

# 4. optional: replace the running copy (stops the wallpaper first)
if ($Install) {
    Get-Process | Where-Object { $_.Path -like "$PSScriptRoot\*" } | Stop-Process -Force
    Start-Sleep 1
    Copy-Item build\LofiDesk.new.exe LofiDesk.exe -Force
    Copy-Item build\LofiDesk.new.exe LofiDesk.scr -Force
    Write-Host 'Installed LofiDesk.exe / LofiDesk.scr'
}
Write-Host 'Build OK'
