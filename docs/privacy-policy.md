# Aria2Gui Privacy Policy

_Last updated: June 2026_

Aria2Gui is a desktop download manager for Windows. This policy describes what the
application does — and deliberately does not do — with your data.

## What we collect

**Nothing.** Aria2Gui contains no telemetry, no analytics, no crash reporting service,
no account system, and no update pings. The developers receive no data from the app.

## Network connections

The only network traffic the application produces is the traffic of the downloads you
add yourself (HTTP/FTP requests, BitTorrent peer/tracker/DHT traffic), performed by the
bundled [aria2](https://aria2.github.io/) engine on your machine. Nothing is sent
anywhere else.

## Local data

All application data stays on your device, next to the executable (portable build) or
in your user profile:

- `settings.json` — your preferences. Proxy/HTTP passwords in it are encrypted with
  Windows DPAPI for the current user.
- `aria2.session` — the download list, so it survives restarts.
- `stats.json` — cumulative transfer totals (bytes only).
- `crash.log` — local error log (size-capped), never transmitted.

Deleting the application folder removes everything.

## BitTorrent visibility

BitTorrent is a peer-to-peer protocol: while you download or seed, your IP address is
visible to other peers and trackers of those torrents, as with any BitTorrent client.
The optional privacy mode forces full peer encryption and disables DHT/PEX/LPD to
reduce passive visibility; it cannot make P2P traffic anonymous.

## Contact

Questions: open an issue on the project repository.
