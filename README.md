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

## Requirements

- Jellyfin 10.11+ (server, `net9.0`).
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

- Enable toggle, **Manage yt-dlp automatically** + **Download / update yt-dlp**
  button, optional **yt-dlp / ffmpeg paths**, **cache directory**.
- **Format selector** (quality/codec), a **Proxy** setting for geo-blocked
  trailers (applied to *both* yt-dlp resolution and the ffmpeg fetch), and
  **extra yt-dlp arguments** (`--cookies` for age-restricted, `--extractor-args`
  for PO tokens, `--limit-rate`, …).
- **Resolve timeout** and **max concurrent builds**.

The build also reconnects automatically on transient network errors, so a
server with an imperfect path to Google's CDN retries rather than failing the
whole trailer.
- **Cache management** — live size/count, a *Clear cache* button, and a daily
  prune task bounded by **max age (days)** and **max size (GB)**.
- Detected **yt-dlp version** display.

## Install

Add the plugin repository to Jellyfin (Dashboard → Plugins → Repositories):

```
https://raw.githubusercontent.com/HarshLabs/jellyfin-plugin-youtube-trailers/main/manifest.json
```

Then install **YouTube Trailers** from the catalog and restart Jellyfin.

## License

MIT — see [LICENSE](LICENSE).
