# YouTube Trailers for Jellyfin

Resolves YouTube trailers **server-side** and serves them as AVPlayer-native
fMP4 HLS, so tvOS / AVKit clients (Apple TV) play one clean URL with **zero
on-device extraction** — for both in-app trailers and the Top Shelf.

## Why

Client-side YouTube extraction (descrambling the player JS on the device) is
slow, CPU-heavy, uncancellable, and breaks whenever YouTube changes. It also
hits googlevideo's IP-binding and ~6-hour URL expiry. Moving resolution to the
server fixes all of that:

- **`yt-dlp`** resolves the best adaptive streams (far more robust than any
  on-device extractor, updated within hours of YouTube changes).
- **`ffmpeg`** stream-copies them into an AVPlayer-native fMP4 **HLS** bundle
  (no re-encode). A live event playlist means fast time-to-first-frame.
- The client only ever talks to your Jellyfin server, so there's no
  IP-binding / expiry — and the Apple TV pays no extraction CPU.

## How it works

The plugin exposes a small set of endpoints under `/Trailers/`:

- `GET /Trailers/{videoId}/main.m3u8` — resolves + remuxes on a cache miss and
  serves the HLS playlist (built incrementally; first segment is ready in a few
  seconds).
- `GET /Trailers/{videoId}/{init.mp4|segN.m4s}` — fMP4 init/media segments
  (HTTP range supported).
- `POST /Trailers/{videoId}/prewarm` — fire-and-forget warm-up.
- `GET /Trailers/health` — capability probe for clients.

Bundles are cached on disk and self-heal: a pruned/cleared bundle is simply
rebuilt on the next request.

Admin-only endpoints under `/Trailers/admin/` back the dashboard page (live
jobs, cache browser, library coverage, failure log, diagnose).

## Automatic trailers for new media

Your metadata providers already store trailer links on every item (TMDb writes
them to **Remote Trailers**), and they're almost always YouTube. The plugin uses
that as its work-list, so nothing has to be scraped or guessed:

- **On library add** — turn on *Cache trailers for newly added media* and every
  newly added or refreshed movie/series gets its trailer built in the
  background, ready before anyone opens it. The lookup is deliberately delayed
  (default 2 minutes): Jellyfin announces a new file *before* the metadata
  provider attaches the trailer link, so resolving immediately would find
  nothing on a fresh import.
- **Daily sweep** — an opt-in scheduled task at 3 AM caches any linked trailer
  that isn't on disk yet. Backstop for items imported while the listener was
  off, whose metadata landed late, or whose bundle was evicted by the pruner.
- **On demand** — the config page lists every item with a trailer link, filtered
  by library and cache state, with per-row and bulk *Cache* actions.

Automatic caching can be **scoped to chosen libraries**, so a Home Videos or
Music Videos library doesn't burn build slots on links nobody will play.

Both paths feed one background queue that respects **max concurrent builds**, so
a first-time sweep of a large library can't swamp the server.

## Monitoring and troubleshooting

The dashboard page shows what's happening and, when a trailer won't download,
why:

- **Active jobs** — every build live, from `resolving` → `queued` → `building`,
  with elapsed time, a real progress percentage and encode speed (parsed from
  ffmpeg's progress stream), bytes and segments written, and per-job **Cancel**.
  A speed well under 1x on a stream copy means googlevideo is throttling the
  read. Recent activity lists what just finished, succeeded or not.
- **Diagnose** — paste a YouTube URL or ID and the plugin walks the entire
  pipeline for that one video: yt-dlp and ffmpeg binaries, YouTube metadata
  (availability, age gate, live status), format selection against your selector,
  a range request to the resolved CDN URL, and finally a real 3-second ffmpeg
  stream-copy. It reports which stage breaks, with the raw tool output. Each
  stage has a completely different fix — update yt-dlp / supply cookies / set a
  proxy / open egress to `*.googlevideo.com` — which is why "no URLs" on its own
  is never enough to act on.
- **Failure log** — the last 100 failures with the stage, exit code, the exact
  command that ran, and the tail of the tool's stderr. Common yt-dlp errors are
  translated into the action that fixes them.
- **Retry blocked videos** — a failed trailer fast-fails for a few minutes so
  clients fall back quickly instead of stalling; this clears that block list
  after you've fixed the underlying cause.
- **Verbose logging** — logs every yt-dlp / ffmpeg command line at Info level.

Cache eviction is driven by **last play**, not build time: serving a playlist
records usage, so a trailer people actually watch survives the age limit while
one nobody has touched is trimmed first.

## Jellyfin version support

The plugin is built for **both** current Jellyfin majors and every release ships
two artifacts:

| Jellyfin | Target framework | `targetAbi` | Artifact |
|---|---|---|---|
| 10.11.x | `net9.0`  | `10.11.0.0` | `youtube-trailers_<ver>_jf10.11.zip` |
| 12.x    | `net10.0` | `12.0.0.0`  | `youtube-trailers_<ver>_jf12.zip` |

Both are listed in `manifest.json` under the same version. Jellyfin filters the
list to entries its own version can satisfy and then takes the newest, so each
server installs its own build automatically — a 10.11 server never sees the 12
entry, and a 12 server prefers the 12 one. Nothing to choose manually.

The source compiles unmodified against both SDKs; there are no `#if` branches
today. `scripts/update-manifest.py` writes the manifest entries (rather than
`jprm repo add`, which de-duplicates by version and would drop one build).

## Requirements

- Jellyfin **10.11+ or 12.x** (server).
- **`yt-dlp`** — **no manual install needed.** The plugin downloads and
  maintains its own official yt-dlp standalone binary for your server's OS/arch
  (Linux x64/arm64/armv7, macOS, Windows) on first start — no Python required.
  You can also pin a system yt-dlp by setting its path in the config.
- **`ffmpeg`** — the server's bundled ffmpeg is used by default.

### yt-dlp management

On a fresh install the plugin fetches the correct yt-dlp build from the
[official yt-dlp releases](https://github.com/yt-dlp/yt-dlp/releases) into its
data folder and uses it automatically. The config page shows the detected
version and has a **Download / update yt-dlp now** button — handy because
YouTube changes periodically break older yt-dlp builds. To use a system-managed
yt-dlp instead (e.g. installed via your package manager), set its absolute path
in **yt-dlp path** and it takes precedence over the managed copy. Auto-management
can be turned off with the **Manage yt-dlp automatically** toggle.

> Note: the managed Linux binary targets glibc (works on the common
> Debian/Ubuntu-based Jellyfin Docker images). On musl systems (Alpine), install
> yt-dlp via the package manager and set the path instead.

## Configuration

Dashboard → Plugins → **YouTube Trailers**:

- **General** — enable toggle, **Manage yt-dlp automatically** + an *Update
  yt-dlp* button, optional **yt-dlp / ffmpeg paths**, **cache directory**.
- **Automatic trailers** — cache on library add (with a metadata-settling
  delay), all-trailers-per-item, the daily library sweep and its per-run cap.
- **Playback & quality** — a **quality preset** (best / 1080p / 720p / 480p, or
  a custom yt-dlp selector). Every preset pins H.264 + AAC, which is both what
  Apple devices decode in hardware and what can be stream-copied into fMP4 —
  choosing VP9/AV1/Opus would silently turn a ~0-CPU remux into a transcode.
  Also: a **Proxy** for geo-blocked trailers (applied to *both* yt-dlp
  resolution and the ffmpeg fetch), **cookies** from a browser profile or a
  `cookies.txt` file, a **build speed limit**, **extra yt-dlp arguments**,
  **max concurrent builds** (with this server's core count shown), **resolve
  timeout**, and the **VOD grace** window.

> **Cookies matter.** "Sign in to confirm you're not a bot" is the single most
> common reason extraction fails, and cookies are always the fix. The browser
> option needs a real browser profile *on the server*; headless and Docker
> installs should export a `cookies.txt` and point the file setting at it.

> **Build speed limit** is applied to ffmpeg, not yt-dlp, because ffmpeg is what
> actually downloads the video here — yt-dlp only resolves the URL, so its own
> `--limit-rate` would do nothing.
- **Cache & diagnostics** — **max age (days)** / **max size (GB)** for the daily
  prune task, how long failures are remembered, the **no-segment timeout** that
  kills a build stuck on a dead CDN edge, and **verbose logging**.

Builds reconnect automatically on transient network errors, so a server with an
imperfect path to Google's CDN retries rather than failing the whole trailer.

## Install

Add the plugin repository to Jellyfin (Dashboard → Plugins → Repositories):

```
https://raw.githubusercontent.com/HarshLabs/jellyfin-plugin-youtube-trailers/main/manifest.json
```

Then install **YouTube Trailers** from the catalog and restart Jellyfin.

## License

MIT — see [LICENSE](LICENSE).
