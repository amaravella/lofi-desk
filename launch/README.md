# Quick-launch shelves

Every file in this folder becomes an icon on the two wooden shelves. Files are sorted by name,
so prefix them with numbers to set the order (`1. Spotify.app`, `2. Browser.lnk`, ...).
The name without the number and extension is shown on hover.

Supported files:

- **`.lnk` / `.url` / `.exe`** — any shortcut or program. Drag one here from the Start menu.
- **`.app`** — a one-line text file with a Store app id, for apps that have no `.exe` shortcut:

  ```
  shell:AppsFolder\SpotifyAB.SpotifyMusic_zpdnekdrxrrzw!Spotify
  ```

  To find the id, run `Get-StartApps` in PowerShell and copy the `AppID` column.

Unread counters (the red badge) are read from window titles like `(3) Telegram`, so they work for
Telegram, Discord and anything else that puts the count in its title.
