# BetterPlay

A Jellyfin 12.x plugin that picks the best version and bitrate for each viewer's connection, and keeps playback from
freezing: it watches every stream, steps quality down before a stall, and back up when the connection allows.

**Status: probe release.** It only records how each app requests and plays video. It never changes playback. The
design is in [docs/BetterPlay-design.md](docs/BetterPlay-design.md).

## Install
1. Jellyfin Dashboard → Plugins → Repositories → **+**
2. Name `BetterPlay`, URL `https://raw.githubusercontent.com/AHouseOfBards/BetterPlay/main/manifest.json`
3. Catalog → BetterPlay → Install, then restart Jellyfin.

Requires Jellyfin 12.0 or newer (12.x).

## Modes
- **Off**: does nothing.
- **Watch only** (default): records playback data and never changes playback.

## For admins
- `GET /BetterPlay/Status`: version and mode.
- `GET /BetterPlay/Probe?minutes=30&kind=playbackinfo|start|progress|stop|delivery|image`: recent events.
- `GET /BetterPlay/Sessions?minutes=180`: one line per playback (startup speed, segment timing).

All need an admin API key. Logs are also written to the plugin's data folder (`probe/*.jsonl`).

## Build
`.\build.ps1 -Version 0.1.0.0 -Changelog "..." [-Publish]` (needs the .NET 10 SDK).
