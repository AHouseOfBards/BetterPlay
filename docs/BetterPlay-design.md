# BetterPlay: design

A server-side Jellyfin plugin that picks the right version and bitrate for each viewer when playback starts. While
the film plays, it watches for buffering and freezes, steps quality down before a stall where it can, recovers
when one happens, and steps back up once the connection allows.

Target: **Jellyfin 12.x** (12.0, 12.1, 12.2, 12.3, 12.4 and later 12.x), **net10.0**. Built against the lowest 12.x
package (`Jellyfin.Controller` 12.0.0) with `targetAbi` 12.0.0.0, so one build loads on every 12.x server. CI also
compiles against each newer 12.x package, to catch API changes before a release. It must work on any server, so
every decision comes from data Jellyfin already has (each version's width, height, bitrate, codec and HDR type),
never from file names.

**Distribution and testing:** every build is published to a plugin repository: a zip as a release asset, plus a
`manifest.json` with guid, name, versions, targetAbi, sourceUrl and MD5 checksum. The user installs and updates it
from the manifest URL (Dashboard → Plugins → Repositories) and tests on the real server. Because of that, every build
must be safe on a live server:
- **Fail open:** an error inside BetterPlay never blocks playback; Jellyfin's normal behaviour takes over.
- **Master switch and modes:** Off / Watch only (log what it would do) / On. New features ship in Watch only first.
- **No changes on install:** a new release never changes playback until its mode is raised.

---

## 1. What we know (verified on the user's server, Oct–Nov 2026)

| Fact | Consequence |
|---|---|
| Clients ask the server for `PlaybackInfo` before playing. Quick play from search or a poster sends no `MediaSourceId`, so Jellyfin Web plays the first version listed. Picking a version on the movie's page sends `MediaSourceId`. | The start-of-play choice works by ordering the list and setting the transcode bitrate. A version the user picked is always respected. |
| Remote test: Jellyfin Web on Chrome with quality on Auto played the 4K Iron Man 3 at 17.6 Mbps as HLS fMP4 (video copied, audio to AAC). | Web already receives the video in chunks, which seamless switching needs. |
| Jellyfin's built-in ABR (`DynamicHlsHelper`) only adds two variants 0.2–2 Mbps lower, only when transcoding, only from the same file, never on the LAN, and Jellyfin Web never asks for it. | Not usable; BetterPlay has to provide its own. |
| Every remote viewer shows as `127.0.0.1` (nginx in front of Jellyfin, `KnownProxies` empty). | Real addresses (nginx `X-Forwarded-For` plus `KnownProxies`) are a **prerequisite** for telling home viewers from remote ones and for remembering a device's network. |
| Remote control: Jellyfin Web and Jellyfin for Android accept play commands, `PlayMediaSource` and `DisplayMessage`. Chromecast, Infuse and Plezy accept none. | Mid-film rescue by restart only works on some clients; the others get the start-of-play choice plus learning for next time. |
| The two Iron Man 3 versions differ by 37 s in runtime (7,870 s vs 7,833 s). | Switching to a **different file** mid-film needs a timeline offset; switching the **same file's** transcode bitrate doesn't. |
| NVENC is set up on the Jellyfin server (GPU model unknown). | Transcoding the same file down to a lower bitrate is affordable, which makes it the safest rescue. |
| Versions of 1,100+ movies are merged and labelled (`2160p`, `1080p`, `IMAX`, `1080p 6.7 Mbps`, editions). | Real test material. Editions are different cuts: **never switch edition automatically**. |

## 2. Components

```
 client ──PlaybackInfo──▶ [Selector] ─── picks version and bitrate ──▶ Jellyfin streams it
   │                          ▲
   │ progress reports         │ bandwidth estimate
   ▼                          │
 [Health monitor] ◀── segment and stream timings ── [Delivery meter] (middleware)
   │  state: Healthy / AtRisk / Stalled / Recovering
   ▼
 [Actions] ── lower or raise bitrate, switch version, restart at position, show a message
   │
 [Store] ── per-device bandwidth history, decision log, version timeline offsets
```

1. **Selector**: on `PlaybackInfo`, chooses the version and the bitrate cap.
2. **Delivery meter**: ASP.NET middleware that times each HLS segment and each direct-stream range response
   (bytes over time), per play session.
3. **Bandwidth estimator**: combines the delivery meter, the client's own cap, LAN/remote, the device's history and the
   server's upload budget into one "safe bitrate" per session.
4. **Health monitor**: a state machine per active session (section 5).
5. **Actions**: the ladder in section 6, limited by what each client supports.
6. **Store**: plugin data folder. Device history, a decision log ("why did it pick that?") and timeline offsets.
7. **Config page and dashboard**: settings, live sessions with their state, and the decision log.

**Fail open**: any BetterPlay error leaves Jellyfin's normal behaviour untouched.

## 3. Choosing at start

Budget = min(client's `MaxStreamingBitrate`, learned safe throughput for this device and network × 0.75, the user's
limit, this stream's share of the server's upload budget). A home viewer with no history gets no BetterPlay limit.

1. Drop versions the device can't show well (from the client's `DeviceProfile`): codec it can't decode, HDR on an SDR
   device, resolution above the display.
2. Keep to the same **edition** as the default or requested version.
3. **Play the best version that fits as-is** (direct play or remux), using peak bitrate with headroom.
4. Nothing fits: transcode, from the version that gives the best picture per transcoded megabit. With a GPU, transcoding
   a 4K file down to the budget beats direct-playing a starved 1080p file once the budget is at least about 2× that
   file's bitrate. That's a setting, `PreferTranscodeQualityGain`.
5. Pick the transcode bitrate: the budget, rounded down to a step (40, 20, 15, 12, 10, 8, 6, 4, 3, 2, 1.5 Mbps).

Example: 1080p at 3 Mbps, 4K at 20 Mbps, connection at 10 Mbps, so budget ≈ 7.5. The 1080p file fits, so it direct-plays.
With the GPU and `PreferTranscodeQualityGain` on, 7.5 ≥ 2 × 3, so the 4K file is transcoded at 6 Mbps.

## 4. Signals

| Signal | Source | Tells us |
|---|---|---|
| Progress reports | `ISessionManager.PlaybackProgress` (about every 10 s on Web; check other clients) | Position not advancing while unpaused means a stall. |
| Segment requests | Delivery meter | Seconds delivered minus position equals the client's **buffer estimate**. Each segment's download time compared with its duration shows whether throughput is below the bitrate. |
| Response throughput | Delivery meter | Live bandwidth, smoothed (EWMA, plus P20 over the last minute). |
| Transcode speed | Transcoding job / ffmpeg progress | Below about 1.05× means the **server** is the bottleneck, not the network. |
| Restarts and errors | Start/stop events | The same item restarted within 2 min, or stopped with an error, means a freeze the user had to work around. |

## 5. Health states

- **Healthy**: buffer above 20 s, or throughput at least 1.3× the bitrate.
- **AtRisk**: buffer below 15 s and falling, with throughput below 1.1× the bitrate for 20 s or more. Step down **before**
  the freeze.
- **Stalled**: position stuck for 5 s or more while unpaused, or buffer at 0.
- **Recovering**: after an action, judged for 30 s.
- **Step up**: only after 3 min Healthy with throughput at least 1.5× the next tier's bitrate. One step at a time, at most
  once every 5 min, so quality doesn't bounce back and forth.

If the server is the bottleneck (transcode below 1×), the fix differs: drop the tone mapping or resolution, or switch
to remuxing a lower version instead of transcoding.

## 6. Actions, by client capability

| Action | Clients | Smoothness |
|---|---|---|
| A1. Start-of-play choice | All that use `PlaybackInfo` | Nothing visible |
| A2. Multi-variant HLS ladder (BetterPlay serves the master playlist; the player switches itself) | HLS players (Web/hls.js, Android/ExoPlayer, Apple, Roku): **check each one** | Seamless |
| A3. Restart at the current position with a lower bitrate or another version | Remote-control clients (Web, Android) | About 2–5 s rebuffer |
| A4. Learn for next time | Everything, including Chromecast, Infuse and Plezy | Next play is right |

**Rescue order:** first, a lower transcode of the **same file** (same timeline, always safe). Second, a **different
file** only when its timeline offset is known (section 7). Optionally show "BetterPlay lowered the quality to keep
playback smooth" through `DisplayMessage`.

## 7. Timeline alignment between versions

Different releases differ in length (logos, credits, sometimes cuts). A background task works out the offset between
each pair of merged versions by comparing their soundtracks at a few points (ffmpeg audio extract plus
cross-correlation). Pairs with one constant offset get stored and become safe for mid-film switching. Pairs whose
offsets vary are marked "no cross-switch".

## 8. Hooks in Jellyfin (to confirm against the 12.2 source in phase 0)

- `IPluginServiceRegistrator`: register services, an MVC action filter on `MediaInfoController.GetPostedPlaybackInfo`,
  and an `IStartupFilter` for the delivery-meter middleware.
- `ISessionManager`: playback events; `SendPlayCommand`, `SendGeneralCommand`, `SendMessageCommand`.
- Transcoding job information, for the speed and bitrate of the running ffmpeg.
- Plugin configuration page (embedded HTML/JS) and the plugin data folder.

## 9. Phases

| # | Phase | Done when |
|---|---|---|
| 0 | Setup: .NET 10 SDK, the repository and its manifest, a build script that versions, zips and checksums each release, and a **probe** release that only logs PlaybackInfo, progress and segment timings. Installed on the real server from the manifest URL. | It installs from the manifest; we know each client's report cadence, whether reordering is respected, and that the hooks work on 12.x |
| 1 | Selector (A1) and device history (A4) | Quick play picks the right version on Web, Android TV and Roku |
| 2 | Health monitor, **watch-only** ("would have acted" in the log) | Its states match real buffering in tests where the connection is throttled |
| 3 | Rescue (A3) and step-up, same-file first | A throttled test recovers without the user doing anything |
| 4 | Seamless ladder (A2); then cross-version switching with alignment | Web and Android TV switch without a stall |
| 5 | Config page, dashboard, packaging as a plugin repository | Installs from a repository URL on any server |

## 10. Prerequisites and open questions

- Real client IPs on the production server (nginx and `KnownProxies`).
- Home upload speed (for the upload budget).
- Which HLS players honour multiple variants (phase 4 check).
- Whether Jellyfin 12.2 keeps an MVC filter's changes to `PlaybackInfo`, or a different hook is needed.
