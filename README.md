# CS2 Drop Checker

A small Windows app that watches your CS2 inventory and shows each new item (weekly drop, case opening, trade-up, market purchase) with its current Steam price in a card on top of the game. It also keeps a history of everything that arrived and tracks your inventory value over time.

It only reads public Steam web data. It does not touch the game process, memory or files.

## Download

Get `cs2-drop-checker.exe` (or the zip) from the [latest release](https://github.com/kaarude/cs2-drop-checker/releases/latest) and run it. Nothing to install: the exe is self-contained (Windows 10/11, x64).

The optional Steam sign-in uses the Microsoft Edge WebView2 Runtime, which ships with Windows 11 and current Windows 10.

## First run

The setup wizard asks for three things:

1. **Your Steam profile.** Paste a profile link (`steamcommunity.com/id/...` or `/profiles/...`), a custom URL name or a SteamID64. The wizard checks that the inventory can be read.
2. **Sign in (optional).** Signing in to Steam inside the app includes trade-protected items, so purchases show up right away instead of after the 7-day trade protection. You sign in on Steam's own login page. The session stays in the app's private browser profile, and the app never sees or stores your password.
3. **Hotkeys and overlay.** Review the hotkeys and where the cards appear.

The inventory must be public if you don't sign in: Steam > Profile > Edit Profile > Privacy Settings > Inventory: Public.

## How it works

- While CS2 is running (or always, if you change that in Settings) the app checks the inventory every 3 minutes (2 to 60).
- Each check is compared with the previous one. Items that left the inventory explain the new ones: a consumed case means a case opening, five or more consumed skins mean a trade-up, a capsule means a capsule opening. Items moved in or out of storage units are not shown as new.
- Every new item gets a card in the overlay and a row in the Drops history, with the price at the time it arrived.

## Overlay

- Click-through and always on top. It never takes focus from the game.
- Run CS2 in **Fullscreen Windowed** (borderless). In exclusive fullscreen Windows can't draw the overlay; items are still detected and saved.
- Position, monitor, size, opacity and seconds per card are in Settings.

Default hotkeys (change them in Settings):

| Hotkey | Action |
| --- | --- |
| Ctrl+Shift+O | Turn the overlay on or off |
| Ctrl+Shift+V | Show the total inventory value |
| Ctrl+Shift+R | Check the inventory now |

## Prices

Sources are tried in the order set in Settings:

| Source | Data |
| --- | --- |
| Steam | Live Steam Community Market price for the item |
| Steam 24h | Daily Steam price feed from [CSGO Trader](https://csgotrader.app/) |
| Skinport | Skinport's public item prices |

Prices are shown in your chosen currency. Feeds in USD are converted with daily ECB rates from [Frankfurter](https://frankfurter.dev/). Items with stickers, charms or Doppler phases can be worth more than the listed price; the card says so.

## Pages

- **Overview:** inventory value over time, best drop, weekly drop count and recent drops (price when it arrived vs. now).
- **Drops:** the full history, filtered by drops, openings, trade-ups or purchases.
- **Inventory:** the current inventory with prices.
- **Lookup:** a one-time look at any public profile's inventory and value. Nothing is saved.
- **Settings:** account, price source order, overlay, hotkeys, start with Windows.

## Data

Settings are in `%APPDATA%\CS2DropChecker`. The database, image cache, logs and the sign-in browser profile are in `%LOCALAPPDATA%\CS2DropChecker`. Delete both folders to reset the app.

## Build from source

Requires the .NET 10 SDK on Windows.

```powershell
dotnet build Cs2DropChecker.slnx
dotnet test Cs2DropChecker.slnx
.\publish.ps1   # writes cs2-drop-checker.exe and cs2-drop-checker.zip to the repo root
```

Projects:

- `src/Cs2DropChecker.Core`: Steam profile and inventory reading, snapshot diff and drop classification, price sources, SQLite storage.
- `src/Cs2DropChecker.App`: WPF app (main window, setup wizard, overlay, tray, hotkeys).
- `tests/Cs2DropChecker.Core.Tests`: unit tests for the core library.

Set `CS2DC_HOME` to a folder to keep all app data there instead (useful for testing).

## License

MIT. Not affiliated with Valve. CS2 and Steam are trademarks of Valve Corporation.
