# Aria2Gui

**English** · [Русский](README.ru.md)

A fast, **portable** download & torrent manager for Windows - a clean, qBittorrent-style desktop front-end for the [aria2](https://aria2.github.io/) engine, built with **WinUI 3**.

No installer, no dependencies: unzip and run. The aria2 engine, the .NET runtime and the Windows App SDK are all bundled inside.

![Aria2Gui - main window](docs/main.png)

## Features

- **qBittorrent-style layout** - a status-filter sidebar, a sortable download table (name, size, progress, status, seeds/peers, down/up speed, ETA, ratio) with resizable columns, and a details pane (General / Files / Peers / Trackers).
- **Add downloads** - HTTP/FTP and magnet links (one per line) and/or a `.torrent` file, with a collapsible **per-file selection tree** (folders, select all / none) so you grab only what you want.
- **Browser integration** - register as the handler for `magnet:` links and `.torrent` files (opt-in), watch a folder for dropped `.torrent` files, or catch a copied magnet link from the clipboard - each opens the add dialog so you still pick the save folder.
- **Speed control** - global and per-torrent limits with selectable units (B/KB/Kb/MB/Mb), plus **slow mode**: an alternative limit pair switched with one click from the toolbar or the tray.
- **Queue management** - move waiting downloads to top/up/down/bottom; the table follows the engine's live order.
- **One-click privacy mode** - forces full peer encryption and disables DHT/PEX/LPD; switching it off restores your previous settings.
- **Seeding modes** - by ratio, by time, or off; per-torrent recheck and **set location** (move files to another folder without losing progress).
- **Instant settings** - Windows 11 style: every change applies and is saved immediately, no Save button. Covers download folder, speed limits, concurrency, connections, proxy (with credentials), timeouts/retries, file allocation, disk cache, BT listen port, DHT/PEX/LPD, encryption (with level), max peers, extra trackers and raw aria2 options.
- **Statistics** - session and all-time totals (downloaded / uploaded / ratio) in a status-bar flyout.
- **12 languages** - English, Русский, Español, Deutsch, Français, Português (BR), Italiano, 中文 (简体), 日本語, Українська, Polski, Türkçe - switchable from inside the app (works in the portable build too).
- **System tray** - minimize or close to tray, start with Windows, start minimized, pause-all and slow-mode toggles, live download tooltip.
- **Drag & drop** - drop links or `.torrent` files straight onto the window.
- **Quality of life** - keeps the computer awake while transfers run; desktop notifications for completed/failed downloads; the download list survives every restart.
- **Themes** - System / Light / Dark, on a Mica backdrop.
- **Truly portable** - self-contained; runs from any folder or USB stick. Settings and the aria2 session live in a `data\` folder next to the executable. One instance per copy (two GUIs would corrupt a shared session).

## Screenshots

| Settings | Add download |
| :---: | :---: |
| ![Settings](docs/settings.png) | ![Add download](docs/add-dialog.png) |

## Download

Grab the latest `Aria2Gui-portable-win-x64.zip` from the [**Releases**](../../releases) page, extract it anywhere, and run `Aria2Gui\Aria2Gui.exe`.

**Requirements:** Windows 10 version 1809 (build 17763) or newer, 64-bit (x64). Nothing to install - the .NET 10 runtime, the Windows App SDK and the aria2 engine ship inside the folder.

## Building from source

Prerequisites: the [.NET 10 SDK](https://dotnet.microsoft.com/download), the Windows 10 SDK, the WinUI / Windows App SDK tooling, and Developer Mode enabled.

```powershell
git clone https://github.com/vidrug/aria2-winui3.git
cd aria2-winui3

# portable, self-contained x64 release
dotnet publish Aria2Gui/Aria2Gui.csproj -c Release -p:PublishProfile=win-x64-portable -p:Platform=x64
# output: publish\Aria2Gui\   →  run Aria2Gui.exe
```

For day-to-day development, open the project in Visual Studio 2022 (17.10+) with the Windows App SDK workload, or build and launch it with the WinUI dev tooling.

## Tech stack

- **WinUI 3** / Windows App SDK 2.1
- **.NET 10** (C#), MVVM via [CommunityToolkit.Mvvm](https://github.com/CommunityToolkit/dotnet)
- CommunityToolkit WinUI controls (SettingsControls, Sizers)
- **aria2 1.36** - bundled engine, driven over its WebSocket JSON-RPC interface on a random loopback port
- Localization through MRT `.resw` resources resolved with an explicit, language-pinned resource context, so the in-app language picker works even in the unpackaged/portable build (where the framework's default `x:Uid` resolution is otherwise stuck on the OS language)

## Localization

UI strings live in `Aria2Gui/Strings/<lang>/Resources.resw` (12 languages). XAML text is localized with the `loc:Localize.Uid` attached property and code-behind via the `L` helper - both resolve through a context pinned to the saved language. To improve a translation, edit the matching `.resw` file; to add a new string, add its key to **all** language files. Contributions of new languages or translation fixes are welcome.

## Notes

- aria2c is launched on a private loopback port with a random secret and is tied to the GUI's lifetime (a Win32 Job Object plus `--stop-with-process`), so it never lingers after the app closes.
- The session is auto-saved, so unfinished downloads resume on the next launch.

## Limitations

Honest notes on what the aria2 engine cannot do compared to qBittorrent/libtorrent-based clients:

- **No sequential/streaming download for torrents** - aria2 has no per-piece prioritization, so you can't watch a video while it downloads.
- **No per-file priorities** - files can be included/excluded (select-file), but not ordered high/normal/low.
- **No IP filter lists** (eMule/PeerGuardian style block lists are not supported by the engine).
- **No super-seeding / per-tracker statistics** - the engine reports the tracker list but not per-tracker health.
- **DHT state is in-memory per session** plus a small routing-table file; node bootstrap after a cold start can take a moment.

## Privacy

The app collects **no telemetry** and makes no network connections of its own - all traffic belongs to the downloads you add. The optional privacy mode forces full peer encryption and disables DHT/PEX/LPD. See [docs/privacy-policy.md](docs/privacy-policy.md).

## Credits & third-party

- **[aria2](https://aria2.github.io/)** - the download engine, bundled as `aria2c.exe`. aria2 is licensed under the **GNU GPL v2 (or later)**; this repository redistributes the unmodified official binary.
- **[Windows App SDK / WinUI 3](https://github.com/microsoft/WindowsAppSDK)** and the **[.NET Community Toolkit](https://github.com/CommunityToolkit)** (MIT).
