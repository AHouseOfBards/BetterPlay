# BetterPlay: what we can intercept, say and do, and what each app allows

Checked against source code in November 2026: Jellyfin server (`master`), jellyfin-web, jellyfin-androidtv,
jellyfin-roku, jellyfin-chromecast, jellyfin-kodi, Swiftfin, plus Firecore staff statements for Infuse. Items
marked *unverified* still need the probe or a source read.

BetterPlay runs **inside** the Jellyfin server. Every HTTP request an app makes passes through our middleware before
Jellyfin handles it, and every response passes through it on the way out. Apps can't tell our changes from
Jellyfin's own behaviour.

---

## 1. Requests we can intercept and rewrite (before Jellyfin sees them)

| Request | What we can change | Why it's useful |
|---|---|---|
| `POST /Items/{id}/PlaybackInfo` | DeviceProfile (remove direct-play profiles, codecs, HDR), `MaxStreamingBitrate`, `EnableDirectPlay/DirectStream`, `MediaSourceId` (only when the user didn't choose one) | Forces a path (HLS vs file), caps bitrate, picks the version for **every app that asks** |
| `master.m3u8` / `main.m3u8` | Query: `VideoBitrate`, `MaxWidth/Height`, `SegmentLength`, `MediaSourceId`, codec | Changes what the transcode produces |
| HLS segment `hls1/main/N.mp4` | The same query parameters, **per segment** | Jellyfin restarts ffmpeg when the requested segment is behind, or more than 24 s past, the encoder (`DynamicHlsController.GetDynamicSegment`); its settings come from the segment request. Rewrite the query and kill the job (`ITranscodeManager.KillTranscodingJobs`) to get **a new bitrate from segment N on: server-side adaptive bitrate on any HLS app** |
| `/Videos/{id}/stream?static=true`, `/Items/{id}/Download` | Which file is served, **at the start of a request** | Version substitution for apps that only read files (Infuse, direct play everywhere). Never mid-file: byte offsets must stay consistent |
| `/Playback/BitrateTest` (Web's speed test) | Measure it ourselves too | A server-side speed sample |
| Progress / start / stop reports | Read only | Position, paused, play method: freeze detection |

## 2. What we can make the server say (responses)

| Response | What we can change |
|---|---|
| PlaybackInfo | Version **order** (Jellyfin's `SortMediaSources` puts the clicked item first, then direct play, then bitrate under the cap: we re-sort after it); **hide** versions for this device; flip `SupportsDirectPlay/DirectStream/Transcoding` (decides the path on Android TV, Roku, Kodi add-on, Web); rewrite `TranscodingUrl` (bitrate, segment length, or **our own endpoint**) |
| `master.m3u8` | Add **several variants** (our bitrate ladder) so the app's player switches by itself: hls.js (Web, default adaptive bitrate on), Media3/ExoPlayer (Android TV, *unverified*), Roku native, Shaka/CAF (Chromecast, *unverified*), AVPlayer (Apple) |
| `main.m3u8` | Playlist type: an EVENT/live-style playlist allows later additions and `EXT-X-DISCONTINUITY`, so switching to **another file or resolution mid-stream** works even on single-variant players |
| Segments | Produced by Jellyfin with rewritten settings, or by **our own ffmpeg pipeline** behind our endpoint (full control: pre-encode the lower tier in parallel for instant switches) |
| **Jellyfin Web's code** (`/web/index.html`) | Inject a BetterPlay script (own middleware, as Projectionist does, or the File Transformation plugin). It runs inside Jellyfin Web **and every app built on it**: Jellyfin for Android (web wrapper), Jellyfin iOS, Jellyfin Media Player, jellyfin-uwp, and possibly webOS/Tizen (*unverified*). Gives exact stall and buffer events, control of hls.js (buffer length: Web only buffers **6 s** ahead at ≥25 Mbps on Chrome, Edge and Firefox), a real speed test, and a quality menu |
| Item and detail responses | Per-viewer version names, hidden versions |

## 3. What we can do on behalf of the server (proactively)

- **Remote commands** (`ISessionManager`):
  - `Play` at a position. This makes the app ask PlaybackInfo again, **which we answer**, so it's a restart into a new
    version or bitrate.
  - `Playstate` (Seek/Stop), `SetMaxStreamingBitrate` (restarts at the current position), `DisplayMessage`.
- **Kill or restart transcode jobs** (forces the next segment to be produced with new settings).
- **Pre-transcode:** encode ahead or in parallel at a lower tier so a switch is instant; warm up the start of a film
  for fast starts.
- **Drop a connection on purpose:** apps that re-ask PlaybackInfo get a new choice. Apps that resume a byte offset
  must keep the same file.

## 4. What we can do on behalf of the client

- No code runs on native apps, but the server can **act as the app's adaptive brain**. It measures every
  segment and byte it delivers (buffer estimate = seconds delivered minus position), decides, and serves the result.
  That needs no app support at all.
- In web-based apps, the injected script **is** client-side code: player events, buffer, speed tests.

## 5. Per app

| App | Asks PlaybackInfo | Which version it plays | Follows server flags and URL | Remote commands | Best mid-film option |
|---|---|---|---|---|---|
| **Jellyfin Web** (and the Android/iOS/desktop/UWP wrappers) | Yes | First after sort, or the user's pick | Yes | Play, **SetMaxStreamingBitrate**, DisplayMessage, PlayMediaSource | Injected script plus multi-variant HLS or server-side ABR: **seamless** |
| **Jellyfin Android TV** | Yes | First suitable, or the user's pick | **Completely** (direct play / remux / transcode only by flags; uses our `TranscodingUrl`) | Play (items + position), Seek/Stop, DisplayMessage; no SetMaxStreamingBitrate | Force HLS, server-side ABR or variants: **seamless**; fallback Play-restart |
| **Jellyfin Roku** | Yes | **Always the first** | Yes (`TranscodingUrl` as given) | *unverified* | Force HLS, server-side ABR or variants (Roku handles adaptive bitrate natively): **seamless** |
| **Chromecast** (Jellyfin receiver) | Yes, itself | `getOptimalMediaSource` | Yes; reads the max bitrate once, no retry on error | Through the casting app (Web/Android) | Server-side ABR or variants: **seamless** |
| **Swiftfin** (iOS/tvOS) | Yes (*details unverified*) | *unverified* | *unverified* | **SetMaxStreamingBitrate** handled | Server-side ABR, or SetMaxStreamingBitrate |
| **Kodi add-on** | Yes (add-on mode) | Own picker when several | Yes: direct path only if direct play is allowed; else static or `TranscodingUrl` | *unverified* | Force HLS, then server-side ABR. **Native mode bypasses Jellyfin entirely** |
| **Infuse** | Reads the library, but plays via static/download | Its own | **No**: no Jellyfin transcoding (Firecore staff) | None | **None mid-film.** Choose the file at the start of the request; detect from delivered bytes; learn for next time |
| Plezy, Streamyfin, Findroid, Wholphin, Moonfin, Fladder, VidHub, Dune, FluentFin | *unverified* | | | | Rule: if it asks PlaybackInfo, it can be steered. The probe will tell |

## 6. Real limits

1. **Byte-exact file reads** (Infuse, any direct play in progress): the content can't change mid-file. Only the start
   of a request can be redirected.
2. **Apps that never ask Jellyfin** (Kodi native paths): invisible.
3. **Cross-file switching mid-film** needs timeline alignment (Iron Man 3's versions differ by 37 s) plus
   discontinuity handling.
4. **Unverified player behaviour** (multi-variant support, reaction to bitrate changes inside one variant, live-style
   playlists): the probe phase tests each app.
5. **Upload bandwidth and GPU sessions** are shared: the server-side budget must account for them.
