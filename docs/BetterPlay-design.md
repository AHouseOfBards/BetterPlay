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

## 0. Goal: one feature set on every client

The core feature is the same on every app: **play the original file when the connection carries it. When it
doesn't, adjust transcode quality step by step to match real network conditions, then step back up toward the
original.** To get the same behaviour everywhere, BetterPlay doesn't rely on what each app can do. It owns delivery
for remote viewers, and changes what the server offers and sends:

| Layer | What BetterPlay does | Why it works on every app |
|---|---|---|
| **Offer (spoof PlaybackInfo)** | For a remote viewer whose device isn't yet proven, answer with "direct play: no, direct stream: yes", so the app takes an HLS stream of the original. Choose the version and order. | Every app plays what the server says it can. Each app decides only from the flags in the answer. |
| **Original-first HLS ("copy rung")** | The stream starts as a remux of the original: video copied, untouched quality, with audio converted only if needed. | Same quality as direct play, but delivered in segments the server controls. |
| **Server-side ladder** | When request timing shows the buffer draining, the next segments are produced by a transcode a step lower (copy, then for example 20, 15, 10, 8, 6, 4 Mbps), cut at the same segment boundaries. Step up the same way, back to copy. | The app keeps fetching the same playlist and segment numbers and never knows. Works on any HLS app, with no app support needed. |
| **Virtual file (apps that only read files: Infuse, Kodi native)** | Answer the file or download request with a live stream in the original container, original quality first, re-encoded lower when reads fall behind. | The app thinks it's reading the file. Seeking maps byte ranges to time (experiment). |
| **Remote command (bonus)** | Web and Swiftfin get `SetMaxStreamingBitrate`; Android TV gets Play-at-position, when a restart is faster than waiting for the ladder. | A last-resort rescue, not the main mechanism. |

**Signal for every app: the timing of the player's requests.** A player asks for the next segment, or the next byte
range, after the previous one arrives. So on a link slower than the video, its requests come further apart than one
segment length. A buffering reverse proxy such as nginx can't hide this, unlike byte timing, which behind a local
proxy only measures the hand-off to the proxy. Progress reports add the position, so buffer ahead = requested video
minus position. Implemented in `Probe/SessionAnalyzer.cs`.

**Hard parts to prove before turning it on:**
1. Switching copy → transcode mid-playlist with matching segment boundaries. Copy segments are cut on the source's
   keyframes; the transcode must force keyframes at the same times.
2. Making a mid-stream change of segment content and bitrate acceptable to each player (Web hls.js, ExoPlayer on
   Android TV, AVPlayer on iOS, Roku).
3. Serving the virtual file to Infuse and Kodi.
4. The GPU and upload budgets when several remote viewers are on the ladder at once.

---

## 1. What we know (verified on the user's server, Oct–Nov 2026)

| Fact | Consequence |
|---|---|
| Clients ask the server for `PlaybackInfo` before playing. Quick play from search or a poster sends no `MediaSourceId`, so Jellyfin Web plays the first version listed. Picking a version on the movie's page sends `MediaSourceId`. | The start-of-play choice works by ordering the list and setting the transcode bitrate. A version the user picked is always respected. |
| Remote test: Jellyfin Web on Chrome with quality on Auto played the 4K Iron Man 3 at 17.6 Mbps as HLS fMP4 (video copied, audio to AAC). | Web already receives the video in chunks, which seamless switching needs. |
| Jellyfin's built-in ABR (`DynamicHlsHelper`) only adds two variants 0.2–2 Mbps lower, only when transcoding, only from the same file, never on the LAN, and Jellyfin Web never asks for it. | Not usable; BetterPlay has to provide its own. |
| Every remote viewer shows as `127.0.0.1`. nginx already sends `X-Forwarded-For`, but `KnownProxies` is empty. | Add `127.0.0.1` to `KnownProxies` (the dashboard warns when this is missing). |
| nginx buffering is on and stays on (the user's choice). Server-side segment delivery measured 100–500+ Mbps to a remote browser. | Byte timing behind a local proxy is the hand-off to nginx, not the viewer, so health comes from request timing (section 0). |
| Copy-mode HLS on Web: 6 s segments, requested steadily about every 6 s once the buffer is full. | A steady gap of about one segment length = keeping up; longer gaps = draining. |
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

## 5a. Ladder control: no rapid notching up and down

Quality changes are rare and deliberate: **step down fast when it matters, step up slowly once it's proven.**

1. **Ladder rungs about 1.5× apart:** copy (original), then 40, 25, 16, 10, 6.5, 4, 2.5 Mbps. Each rung is a visible
   difference, so there's never a reason to hop between near-identical rungs. Each step down costs one encoder
   restart.
2. **Asymmetric rules:**
   - **Down:** when the buffer is under 15 s and falling, or 2 or more late requests in the last minute. Go straight
     to the rung under 0.8 × measured throughput, skipping rungs if needed, so one step down is enough.
   - **Up:** one rung at a time, only after at least 3 min at the current rung with the buffer full (25 s or more)
     and measured throughput at least 1.5 × the next rung's bitrate. The **copy rung needs the highest bar**:
     throughput at least 1.3 × the original's peak segment rate (not its average), held for 5 min.
3. **Minimum time on a rung:** 60 s after any change before another step down, unless Stalled, and 3 min before any
   step up. At most 4 changes in 10 min. After that, hold the lower rung for 10 min.
4. **Back-off on failed step-ups:** if a step up is followed by a step down within 2 min, that rung is "failed". The
   wait before trying it again doubles each time (3, 6, 12, then 20 min at most). It's remembered per device and
   network, so the next film starts on the right rung.
5. **Throughput estimate, conservative:** two moving averages, fast (half-life about 2 segments) and slow (half-life
   about 10 segments).
   - Step down on the lower of the two.
   - Step up only on the slow one, and only when the fast one agrees.
   - A brief dip with a full buffer is ignored, because the buffer covers it.
6. **Headroom can only be measured while the player fetches flat out:**
   - **What request timing can't show:** with a full buffer, the player asks once per segment length. That proves
     "at least the current bitrate" but not how much more the link could carry.
   - **Where headroom shows:** during the startup burst, the refill after a seek, and the refill after a dip. Use
     segment bytes over the gap between requests.
   - **What a step up waits for:** a recent burst measurement. With none in the last 10 min, BetterPlay stays put
     instead of guessing.
7. **Bitrate per segment, not on paper:** VBR originals have peaks, so judge each rung by its real bytes per second
   of video over the last segments, not by its nominal bitrate.
8. **Server-side bottleneck:** if the encoder runs below 1.05 × real time, more bandwidth won't help. Step resolution
   or tone mapping down instead, and don't count it as a network failure.

## 5b. Silent stops and failed starts (not network)

**What the user sees:** sometimes a file won't start, or stops mid-play with no loading spinner. That usually means the
player isn't waiting for data; something it asked for failed or never arrived. Typical causes:
- the server couldn't read the file (slow or failing disk, network share hiccup);
- ffmpeg died or stopped producing;
- a segment request returned an error;
- the file's media info is missing (failed probe), so the server offers nothing playable.

**Evidence on the user's setup (2026-11-04):**
- Media host drive E: (USB external, "Disk 1", 98% full) logged 92 read retries in 3 days. It holds every "… 1"
  library folder.
- The Jellyfin server logged SMB errors ("insufficient system resources") on its folder watchers, and failed probes
  ("streams and format are both null") on normal files during scans.

**What BetterPlay adds (incident capture):**
- **Incident triggers:**
  - Stalled for more than 5 s;
  - a stop without completion within 30 s of a stall;
  - the same item requested again within 2 min;
  - any delivery with status 400 or higher;
  - a segment whose first byte takes more than 2 × the segment length.
- **What each incident records:**
  - the session's last 30 requests (status, time to first byte, bytes);
  - the progress around the incident;
  - the source file's path and drive;
  - the active ffmpeg log name;
  - server log errors within ±30 s.
- **Time to first byte stays meaningful behind nginx.** It measures how long Jellyfin took to produce the response:
  disk reads and ffmpeg. So slow or failing storage shows up even though throughput is hidden.
- **Dashboard "Incidents" list:** per incident, the likely cause (storage, transcoder, request error, network,
  missing media info), plus counts per drive and library so a failing disk stands out.
- **Rescue for read stalls (later, Mode = On):** when a segment's first byte is late because the source is slow, and
  another version of the title exists on a different drive, the next restart uses that version.

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
