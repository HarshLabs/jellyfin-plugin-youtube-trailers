using System.IO;
using System.Threading;
using System.Threading.Tasks;
using Jellyfin.Plugin.YouTubeTrailers.Services;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Mvc;
using Microsoft.Extensions.Logging;

namespace Jellyfin.Plugin.YouTubeTrailers.Api;

[ApiController]
[Authorize]
[Route("Trailers")]
public sealed class TrailerHlsController : ControllerBase
{
    private readonly TrailerResolver _resolver;
    private readonly YtDlpManager _ytDlp;
    private readonly ILogger<TrailerHlsController> _logger;

    public TrailerHlsController(TrailerResolver resolver, YtDlpManager ytDlp, ILogger<TrailerHlsController> logger)
    {
        _resolver = resolver;
        _ytDlp = ytDlp;
        _logger = logger;
    }

    /// <summary>
    /// Cheap liveness probe so clients can detect plugin availability without
    /// triggering a resolve+remux. Returns 200 + a small JSON body when enabled.
    /// </summary>
    [HttpGet("health")]
    [HttpHead("health")]
    public IActionResult Health()
    {
        _logger.LogInformation("[YouTubeTrailers] health {Method} from UA='{UA}'",
            Request.Method, Request.Headers.UserAgent.ToString());
        if (Plugin.Instance is null || !Plugin.Instance.Configuration.Enabled)
        {
            return NotFound();
        }
        return Ok(new { status = "ready", plugin = "YouTubeTrailers", version = "0.1.0.0" });
    }

    /// <summary>
    /// Returns an AVPlayer-native fMP4 HLS (live event) playlist for a YouTube
    /// video ID. On a cache miss it starts the resolve+remux job and returns as
    /// soon as the first segment is ready; AVPlayer reloads the event playlist
    /// to pick up later segments until EXT-X-ENDLIST appears.
    /// </summary>
    [HttpGet("{videoId}/main.m3u8")]
    [HttpHead("{videoId}/main.m3u8")]
    [Produces("application/vnd.apple.mpegurl")]
    public async Task<IActionResult> GetPlaylist(
        [FromRoute] string videoId, [FromQuery] bool complete, CancellationToken cancellationToken)
    {
        _logger.LogInformation("[YouTubeTrailers] main.m3u8 {Method} for {VideoId} | UA='{UA}'",
            Request.Method, videoId, Request.Headers.UserAgent.ToString());
        if (Plugin.Instance is null || !Plugin.Instance.Configuration.Enabled)
        {
            return NotFound();
        }
        if (!TrailerResolver.IsValidVideoId(videoId))
        {
            return NotFound();
        }

        if (!await _resolver.StartIfNeededAsync(videoId, cancellationToken).ConfigureAwait(false))
        {
            return NotFound();
        }
        if (!await _resolver.WaitForPlayableAsync(videoId, cancellationToken).ConfigureAwait(false))
        {
            return NotFound();
        }

        // Full-screen clients pass ?complete=1 to get a finite VOD playlist (real
        // scrubber) instead of AVKit's live UI. Wait (bounded) for the remux to
        // finish; on timeout fall through and serve the still-live playlist so a
        // slow trailer still plays (it just shows the live UI as before).
        if (complete)
        {
            await _resolver.WaitForCompleteAsync(videoId, cancellationToken).ConfigureAwait(false);
        }

        var playlist = await System.IO.File.ReadAllTextAsync(_resolver.PlaylistPath(videoId), cancellationToken)
            .ConfigureAwait(false);
        playlist = TrailerResolver.FinalizePlaylistType(playlist);
        playlist = TrailerResolver.InjectAuth(playlist, ExtractToken(Request));

        Response.Headers["Cache-Control"] = "no-store, max-age=0";
        return Content(playlist, "application/vnd.apple.mpegurl");
    }

    /// <summary>
    /// Serves an fMP4 init segment or media segment with HTTP range support.
    /// Waits briefly for a not-yet-written segment while its remux job is live.
    /// </summary>
    [HttpGet("{videoId}/{file:regex(^(init\\.mp4|seg\\d+\\.m4s)$)}")]
    [HttpHead("{videoId}/{file:regex(^(init\\.mp4|seg\\d+\\.m4s)$)}")]
    public async Task<IActionResult> GetSegment(
        [FromRoute] string videoId, [FromRoute] string file, CancellationToken cancellationToken)
    {
        // Debug-level: a single trailer is 20-40 segment GETs; keep it off the
        // default Info log to avoid spam, but available when diagnosing.
        _logger.LogDebug("[YouTubeTrailers] segment {Method} {File} for {VideoId} Range='{Range}'",
            Request.Method, file, videoId,
            string.IsNullOrEmpty(Request.Headers.Range.ToString()) ? "-" : Request.Headers.Range.ToString());
        if (Plugin.Instance is null || !Plugin.Instance.Configuration.Enabled)
        {
            return NotFound();
        }
        if (!TrailerResolver.IsValidVideoId(videoId))
        {
            return NotFound();
        }

        var path = _resolver.FilePath(videoId, file);
        if (!System.IO.File.Exists(path))
        {
            // ffmpeg may not have written this segment yet — wait while the job runs.
            if (!await _resolver.WaitForFileAsync(videoId, file, cancellationToken).ConfigureAwait(false))
            {
                return NotFound();
            }
        }

        var stream = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.Read);
        return File(stream, "video/mp4", enableRangeProcessing: true);
    }

    /// <summary>
    /// Fire-and-forget warm-up: starts the resolve+remux job and returns 202
    /// immediately. Both the hero carousel and Top Shelf call this when they
    /// pre-cache a video ID so the trailer is a warm hit by the time it plays.
    /// </summary>
    [HttpPost("{videoId}/prewarm")]
    public IActionResult Prewarm([FromRoute] string videoId)
    {
        if (Plugin.Instance is null || !Plugin.Instance.Configuration.Enabled)
        {
            return NotFound();
        }
        if (!TrailerResolver.IsValidVideoId(videoId))
        {
            return BadRequest();
        }

        // Detached from the request lifetime — must not be cancelled when the
        // POST returns. Errors are logged inside the resolver.
        _ = Task.Run(() => _resolver.StartIfNeededAsync(videoId, CancellationToken.None));
        return Accepted();
    }

    /// <summary>Cache stats for the admin config page (size + bundle count).</summary>
    [HttpGet("admin/stats")]
    [Authorize(Policy = "RequiresElevation")]
    public IActionResult CacheStats()
    {
        var (bytes, count) = _resolver.CacheStats();
        return Ok(new { bytes, megabytes = bytes / (1024 * 1024), count });
    }

    /// <summary>Clears the trailer cache (admin config page button).</summary>
    [HttpPost("admin/clear")]
    [Authorize(Policy = "RequiresElevation")]
    public IActionResult ClearCache()
    {
        var removed = _resolver.ClearCache();
        _logger.LogInformation("[YouTubeTrailers] Cache cleared via admin: {Removed} bundle(s) removed", removed);
        return Ok(new { removed });
    }

    /// <summary>Installed yt-dlp version (admin config page diagnostic).</summary>
    [HttpGet("admin/version")]
    [Authorize(Policy = "RequiresElevation")]
    public async Task<IActionResult> ToolVersion(CancellationToken cancellationToken)
    {
        var version = await _resolver.YtDlpVersionAsync(cancellationToken).ConfigureAwait(false);
        return Ok(new { ytDlp = version, managed = !_ytDlp.UsingConfigured });
    }

    /// <summary>
    /// Downloads / updates the plugin-managed yt-dlp binary (admin config page
    /// button). Returns the new version on success.
    /// </summary>
    [HttpPost("admin/ytdlp/update")]
    [Authorize(Policy = "RequiresElevation")]
    public async Task<IActionResult> UpdateYtDlp(CancellationToken cancellationToken)
    {
        var (ok, message) = await _ytDlp.DownloadAsync(cancellationToken).ConfigureAwait(false);
        var version = ok ? await _resolver.YtDlpVersionAsync(cancellationToken).ConfigureAwait(false) : "error";
        return Ok(new { ok, message, version });
    }

    private static string ExtractToken(HttpRequest request)
    {
        if (request.Query.TryGetValue("api_key", out var q) && !string.IsNullOrEmpty(q))
        {
            return q!;
        }
        if (request.Headers.TryGetValue("X-Emby-Token", out var h) && !string.IsNullOrEmpty(h))
        {
            return h!;
        }
        return string.Empty;
    }
}
