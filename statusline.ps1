# Claude Code status line for Lofi Desk: saves your plan limits for the wallpaper and prints
# a short line like "Opus · 5h 42% · 7d 18%". Claude Code passes the session JSON on stdin
# (rate_limits is present on Pro/Max plans). Add to ~/.claude/settings.json:
#   "statusLine": { "type": "command", "command": "powershell -NoProfile -ExecutionPolicy Bypass -File C:/path/to/LofiDesk/statusline.ps1", "refreshInterval": 60 }
$ErrorActionPreference = 'SilentlyContinue'
[Console]::OutputEncoding = [Text.Encoding]::UTF8
$raw = [Console]::In.ReadToEnd()
try { $j = $raw | ConvertFrom-Json } catch { exit 0 }

$dir = Join-Path $env:LOCALAPPDATA 'LofiDesk'
New-Item -ItemType Directory -Force $dir | Out-Null
$file = Join-Path $dir 'usage.json'

$rl = $j.rate_limits
if ($rl) {
    $out = [ordered]@{ updated = [DateTimeOffset]::UtcNow.ToUnixTimeSeconds() }
    foreach ($k in 'five_hour', 'seven_day') {
        $w = $rl.$k
        if ($w -and $w.used_percentage -ne $null) { $out[$k] = [ordered]@{ pct = [double]$w.used_percentage; resets = [long]$w.resets_at } }
    }
    $tmp = "$file.tmp"
    [IO.File]::WriteAllText($tmp, ($out | ConvertTo-Json -Compress), (New-Object Text.UTF8Encoding $false))
    Move-Item -Force $tmp $file
}

$parts = @()
if ($j.model.display_name) { $parts += $j.model.display_name }
if ($rl.five_hour.used_percentage -ne $null) { $parts += ('5h {0:0}%' -f $rl.five_hour.used_percentage) }
if ($rl.seven_day.used_percentage -ne $null) { $parts += ('7d {0:0}%' -f $rl.seven_day.used_percentage) }
[Console]::Out.Write(($parts -join ' · '))