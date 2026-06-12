# Changelog

## 1.1.0 — 2026-06-12

### Added
- Queue ordering: move waiting downloads to top/up/down/bottom from the context menu; the
  table now follows the engine's live order (active → queue → finished).
- Slow mode ("turtle"): a second pair of speed limits switched with one click from the
  toolbar or the tray menu.
- Transfer statistics: session and all-time totals (downloaded/uploaded/ratio) in a
  status-bar flyout, persisted across runs.
- Keep-awake: the system no longer sleeps while downloads or seeding are active (toggle).
- Tray lifecycle: start with Windows, start minimized to tray, close-to-tray.
- magnet: link and .torrent file association (per-user, opt-in): clicking a link or file
  opens the add dialog pre-filled — pick the folder and files like a manual add.
- Watch folder: .torrent files dropped into a chosen folder are added automatically.
- Clipboard catcher (opt-in): a copied magnet link offers to add itself.
- Trackers tab in the details pane, with per-torrent "Add trackers".
- Set location: move a torrent's files to another folder from the context menu.
- Per-torrent speed limits with selectable units (B/KB/Kb/MB/Mb), slider + free input.
- One-click privacy mode: forces full encryption, disables DHT/PEX/LPD, restores the
  previous settings when switched off.
- Seeding modes: by ratio, by time, or off.
- Auto-recovery: torrents whose control files were lost are re-checked automatically on
  startup instead of dropping into an error.

### Fixed
- The download list survives every exit: completed/seeded torrents are now kept in the
  saved session (`--force-save`), closing the long-standing "list is empty after
  restart" data loss.
- Two full review rounds (60+ confirmed findings): crash-safety in the torrent parser,
  correct ratio display/sorting, file deletion only after the engine confirms removal,
  recheck preserving file selection and limits, settings validation that can't block
  unrelated changes, immediate pause (forcePause), proxy/credential clearing reaching
  the running engine, accessibility names, themed zebra striping, and many smaller
  hardening fixes.
- Passwords are stored DPAPI-encrypted instead of plaintext.

## 1.0.0

- Initial release: qBittorrent-style WinUI 3 interface over the aria2 engine — torrents
  (magnet/.torrent, per-file selection, peers), HTTP/FTP downloads, live speeds, filters,
  sortable columns, details pane, tray icon, 12 UI languages, portable self-contained
  build.
