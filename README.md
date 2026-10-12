# Lofi Desk

**A cozy live wallpaper for Windows with a tiny Claude Code companion living on your desktop.**

A pixel Clawd watches your Claude Code sessions, cheers when a task is done, waves when Claude has a
question, builds himself a little house, climbs ladders, blows soap bubbles and fishes bugs out of the
activity feed. Around him: a lo-fi scene, a music player, weather, notes, a pomodoro and quick-launch
shelves. When you step away, your OLED monitor goes pure black.

*English · [Русский](README.ru.md)*

> Unofficial fan project. Not affiliated with or endorsed by Anthropic. Built entirely in conversation
> with Claude Code.

<p align="center"><img src="docs/tricks.gif" alt="The mascot climbs a ladder to the weather widget, does a cartwheel, a magic trick and goes home" width="640"></p>

<p align="center">
  <img src="docs/overview.png" alt="The whole wallpaper on a vertical monitor" width="300">
  &nbsp;
  <img src="docs/house.png" alt="Inside the mascot's house" width="460">
</p>

## Features

**The mascot (Clawd)**
- Lives on top of the Claude Code activity feed and reacts to it: types when Claude runs tools, looks
  around when it reads files, says **“done: …”** when a session finishes and **“question: …”** when one
  needs you. Click the sign to open that session in the Claude desktop app.
- 60+ animations: juggling, skateboarding, paper planes, chasing butterflies, ladders, balloons, cartwheels,
  moonwalking, fishing bugs out of the feed, soap bubbles, magic tricks, stargazing at night, jumping into
  autumn leaves, splashing in puddles when it rains…
- Dresses for the weather (umbrella, scarf, beanie, sunglasses), carries a lantern at night, wears
  headphones while you stream.
- Pet him by moving the cursor over him, drag him around, select him with a rubber band.
- **His house.** He goes home to rest and sleep. Click the house to peek inside: a furnished room where he
  reads, plays games, waters his monstera, makes tea, fixes the fairy lights and talks to the fish.
  Click the furniture to tell him what to do.

**The desktop**
- Two scenes: **lofi** (rainy window, cat, city lights) and **art** (your own picture, gently animated).
- Music player for anything that reports to Windows media controls (Spotify, browsers, players): cover,
  progress, seek, volume, shuffle/repeat, lyrics search, an audio-reactive wave.
- Weather with hourly forecast (Open-Meteo, no key), system stats (CPU, GPU, RAM, VRAM).
- Sticky notes, a pomodoro, quick timers, quick-launch shelves with unread badges.
- **“I'm away”** button: pauses music and the pomodoro, sends the mascot home and turns the OLED black;
  when you are back you get a summary of what finished while you were gone.
- **Screensaver**: the primary monitor goes pure black (OLED-friendly), the others show the scene.
- Stream mode (OBS running): private things in the feed and notes get blurred.
- Claude plan limits in the feed header.

## Requirements

- Windows 10 or 11, x64. Best with two monitors (the scene lives on the secondary one), works with one.
- WebView2 Runtime (built into Windows 11; [download for Windows 10](https://developer.microsoft.com/microsoft-edge/webview2/)).
- [Claude Code](https://claude.com/claude-code) (terminal or desktop app) for the feed and the mascot's reactions.
  Without it you still get the wallpaper.

## Install

1. Download the latest release zip and unpack it somewhere permanent, e.g. `C:\Users\you\LofiDesk`
   (or `git clone` this repository — it builds itself on first install).
2. Right-click `install.ps1` → **Run with PowerShell**, or run:
   ```powershell
   powershell -ExecutionPolicy Bypass -File install.ps1
   ```
   It creates `settings.ini`, starts Lofi Desk with Windows, registers the screensaver and sets a black
   static wallpaper on the primary monitor. Skip parts with `-NoScreensaver` / `-NoBlackWallpaper`.
3. Edit `settings.ini` (city, language, scene) and choose **Restart wallpaper** in the tray menu.

The exe is not code-signed, so Windows SmartScreen may warn you the first time; you can always build it
yourself from source with `build.ps1`.

## Settings (`settings.ini`)

| key | values | |
|---|---|---|
| `lang` | `en`, `ru` | interface language |
| `scene` | `lofi`, `art` | background scene |
| `art` | `art/your-picture.jpg` | picture for the art scene (see [art/README.md](art/README.md)) |
| `feed` | `1`, `0` | Claude Code feed and the mascot |
| `grain` | `1`, `0` | film grain |
| `city`, `lat`, `lon` | e.g. `Lisbon`, `38.72`, `-9.14` | weather; empty `lat`/`lon` hides the widget |
| `limits` | `0`, `1` | also read plan limits from the Claude desktop app cache (undocumented, off by default) |

Quick-launch icons come from the [`launch/`](launch/README.md) folder.

## How to use

<img src="docs/menu.png" alt="The mascot's menu" width="420" align="right">

- **Click the mascot** → menu: timers, screenshot, clipboard → note, downloads, lock, “I'm away”,
  “do a trick”, “go home”, rearrange the shelves.
- **Click the house** → look inside; **click furniture** inside → he uses it.
- **Click a session** in the feed → opens it in the Claude desktop app.
- **Click just above the floor or a shelf** → he walks or jumps there.
- **Tray icon** → I'm away / I'm back, restart, settings, quit.

### Plan limits

- **Claude Code in a terminal:** add the bundled status line to `~/.claude/settings.json` and the
  wallpaper shows your 5-hour and weekly limits:
  ```json
  "statusLine": { "type": "command", "command": "powershell -NoProfile -ExecutionPolicy Bypass -File C:/path/to/LofiDesk/statusline.ps1", "refreshInterval": 60 }
  ```
- **Claude desktop app:** it does not run status lines. With `limits=1` Lofi Desk reads the 5-hour window
  from the app's local cache instead; the app only stores it when usage is high.

## Privacy

Everything runs locally. The feed reads Claude Code transcripts from `~/.claude/projects` on your disk and
nothing is sent anywhere. The only network requests are the weather (Open-Meteo, every 15 minutes) and
a ping every 15 seconds to show whether you are online.

## Uninstall

Run `uninstall.ps1`, then delete the folder.

## Building from source

```powershell
powershell -ExecutionPolicy Bypass -File build.ps1            # build\LofiDesk.new.exe + scene.en.html
powershell -ExecutionPolicy Bypass -File build.ps1 -Install   # and replace the running copy
```

No SDK needed: it uses the C# compiler that ships with Windows (.NET Framework 4.x) and downloads the
WebView2 SDK from NuGet once.

| file | |
|---|---|
| `LofiSaver.cs` | host: modes (`/w` wallpaper, `/s` screensaver, `/t` test), desktop layering, mouse, Claude Code feed |
| `Widgets.cs` | media, audio level, system stats, weather, launch shelves, notes, tray, downloads, internet |
| `AppUsage.cs` | optional: reads the Claude app's local storage (LevelDB + Snappy) for plan limits |
| `scene.html` | everything you see: scenes, widgets, the mascot and his house (Russian original) |
| `i18n/en.json`, `tools/i18n.cs` | translation: the build generates `scene.en.html` from the dictionary |

**Translations.** UI strings live in `scene.html` in Russian; `tools/i18n.cs` extracts them
(`build\i18n.exe extract scene.html i18n\strings.json`) and `build.ps1` writes `scene.en.html` from
`i18n/en.json`. Code comments are in Russian.

## Known limitations

- Made for and tested on Windows 11 24H2 with two monitors; other setups may need tweaks.
- Some programs (game recorders, automation tools) send synthetic key presses, which makes Windows think
  you are always active; that is why idle detection and the screensaver look at the mouse only.
- The art scene needs your own picture — none is bundled.

## Credits

- Weather data by [Open-Meteo.com](https://open-meteo.com/) (CC BY 4.0).
- [Microsoft Edge WebView2](https://developer.microsoft.com/microsoft-edge/webview2/) SDK, downloaded at build time under its own license.
- Clawd is inspired by the Claude Code terminal mascot. Claude and Claude Code are trademarks of Anthropic.
  This is an independent fan project.

## License

[MIT](LICENSE)
