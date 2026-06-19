using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Linq;
using System.Text;
using System.Text.RegularExpressions;
using System.Threading;
using System.Threading.Tasks;
using MediaBrowser.Common.Configuration;
using MediaBrowser.Controller.MediaEncoding;
using Microsoft.Extensions.Logging;

namespace Jellyfin.Plugin.YouTubeTrailers.Services;

/// <summary>
/// Resolves a YouTube video ID to an AVPlayer-native fMP4 HLS bundle via
/// yt-dlp (URL resolution) + ffmpeg (stream-copy remux). Uses a LIVE event
/// playlist: ffmpeg writes segments incrementally and the server serves
/// segment 0 the moment it exists, so time-to-first-frame is ~resolve + one
/// segment regardless of trailer length, and googlevideo's ~1x read throttle
/// stops mattering (AVPlayer consumes at 1x while ffmpeg stays ahead).
/// </summary>
public sealed class TrailerResolver
{
    // YouTube IDs are exactly 11 chars of [A-Za-z0-9_-]. Validating up front
    // is both correctness and the injection guard for the shell-out below.
    private static readonly Regex VideoIdPattern = new("^[A-Za-z0-9_-]{11}$", RegexOptions.Compiled);
    private const string InitName = "init.mp4";
    private const string PlaylistName = "main.m3u8";

    private readonly ILogger<TrailerResolver> _logger;
    private readonly IMediaEncoder _mediaEncoder;
    private readonly IApplicationPaths _appPaths;
    private readonly YtDlpManager _ytDlp;

    private readonly ConcurrentDictionary<string, SemaphoreSlim> _gates = new();
    private readonly ConcurrentDictionary<string, TrailerJob> _jobs = new();
    // Caps concurrent ffmpeg remuxes so prewarming a shelf can't spawn a swarm.
    // Sized from MaxConcurrentBuilds at construction (restart to change).
    private readonly SemaphoreSlim _startSlots;

    public TrailerResolver(ILogger<TrailerResolver> logger, IMediaEncoder mediaEncoder, IApplicationPaths appPaths, YtDlpManager ytDlp)
    {
        _logger = logger;
        _mediaEncoder = mediaEncoder;
        _appPaths = appPaths;
        _ytDlp = ytDlp;
        var maxConcurrent = Math.Clamp(Plugin.Instance?.Configuration.MaxConcurrentBuilds ?? 4, 1, 16);
        _startSlots = new SemaphoreSlim(maxConcurrent, maxConcurrent);
    }

    public static bool IsValidVideoId(string videoId) => VideoIdPattern.IsMatch(videoId);

    private string CacheRoot
    {
        get
        {
            var cfg = Plugin.Instance?.Configuration;
            return cfg is not null && !string.IsNullOrWhiteSpace(cfg.CacheDirectory)
                ? cfg.CacheDirectory
                : Path.Combine(_appPaths.CachePath, "youtube-trailers");
        }
    }

    public string BundleDir(string videoId) => Path.Combine(CacheRoot, videoId);

    public string PlaylistPath(string videoId) => Path.Combine(BundleDir(videoId), PlaylistName);

    public string FilePath(string videoId, string fileName) => Path.Combine(BundleDir(videoId), fileName);

    // ---- Cache management (config page + daily prune task) ----------------

    /// <summary>Total cache size in bytes and the number of cached bundles.</summary>
    public (long Bytes, int Count) CacheStats()
    {
        var root = CacheRoot;
        if (!Directory.Exists(root))
        {
            return (0, 0);
        }
        long bytes = 0;
        var count = 0;
        foreach (var dir in Directory.EnumerateDirectories(root))
        {
            count++;
            foreach (var file in Directory.EnumerateFiles(dir, "*", SearchOption.AllDirectories))
            {
                try { bytes += new FileInfo(file).Length; } catch { /* racing prune */ }
            }
        }
        return (bytes, count);
    }

    /// <summary>Deletes every cached bundle except those with an active build.</summary>
    public int ClearCache()
    {
        var root = CacheRoot;
        if (!Directory.Exists(root))
        {
            return 0;
        }
        var removed = 0;
        foreach (var dir in Directory.EnumerateDirectories(root))
        {
            var id = Path.GetFileName(dir);
            if (IsBuilding(id))
            {
                continue;
            }
            try { Directory.Delete(dir, recursive: true); removed++; } catch { /* in use */ }
        }
        return removed;
    }

    /// <summary>
    /// Evicts bundles older than <paramref name="maxAgeDays"/> (by last write),
    /// then least-recently-used bundles until under <paramref name="maxBytes"/>.
    /// 0 disables the respective limit. Skips bundles with an active build.
    /// </summary>
    public int PruneCache(int maxAgeDays, long maxBytes)
    {
        var root = CacheRoot;
        if (!Directory.Exists(root))
        {
            return 0;
        }

        var bundles = new List<(string Dir, string Id, DateTime LastWrite, long Size)>();
        foreach (var dir in Directory.EnumerateDirectories(root))
        {
            var id = Path.GetFileName(dir);
            if (IsBuilding(id))
            {
                continue;
            }
            long size = 0;
            var lastWrite = DateTime.MinValue;
            foreach (var file in Directory.EnumerateFiles(dir, "*", SearchOption.AllDirectories))
            {
                try
                {
                    var fi = new FileInfo(file);
                    size += fi.Length;
                    if (fi.LastWriteTimeUtc > lastWrite) lastWrite = fi.LastWriteTimeUtc;
                }
                catch { /* racing */ }
            }
            bundles.Add((dir, id, lastWrite, size));
        }

        var removed = 0;
        var nowUtc = DateTime.UtcNow;

        // 1) Age-based eviction.
        if (maxAgeDays > 0)
        {
            var cutoff = nowUtc.AddDays(-maxAgeDays);
            for (var i = bundles.Count - 1; i >= 0; i--)
            {
                if (bundles[i].LastWrite < cutoff)
                {
                    if (TryDeleteDir(bundles[i].Dir)) { removed++; }
                    bundles.RemoveAt(i);
                }
            }
        }

        // 2) Size-cap eviction, least-recently-used first.
        if (maxBytes > 0)
        {
            var total = 0L;
            foreach (var b in bundles) total += b.Size;
            if (total > maxBytes)
            {
                foreach (var b in bundles.OrderBy(b => b.LastWrite))
                {
                    if (total <= maxBytes) break;
                    if (TryDeleteDir(b.Dir)) { removed++; total -= b.Size; }
                }
            }
        }
        return removed;
    }

    /// <summary>
    /// Returns the installed yt-dlp version string (for the config page), or a
    /// short status ("not found" / "error") so admins can spot a stale or
    /// missing binary — the usual cause when extraction suddenly breaks.
    /// </summary>
    public async Task<string> YtDlpVersionAsync(CancellationToken ct)
    {
        var ytDlp = _ytDlp.Resolve();
        if (ytDlp is null)
        {
            return "not found";
        }
        var psi = new ProcessStartInfo
        {
            FileName = ytDlp,
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            UseShellExecute = false,
        };
        psi.ArgumentList.Add("--version");
        try
        {
            using var timeoutCts = CancellationTokenSource.CreateLinkedTokenSource(ct);
            timeoutCts.CancelAfter(TimeSpan.FromSeconds(5));
            var (exit, stdout, _) = await RunProcessAsync(psi, timeoutCts.Token).ConfigureAwait(false);
            return exit == 0 && stdout.Trim().Length > 0 ? stdout.Trim() : "error";
        }
        catch
        {
            return "error";
        }
    }

    private bool IsBuilding(string videoId) =>
        _jobs.TryGetValue(videoId, out var job) && !job.RunTask.IsCompleted;

    private bool TryDeleteDir(string dir)
    {
        try { Directory.Delete(dir, recursive: true); return true; }
        catch (Exception ex) { _logger.LogDebug(ex, "[YouTubeTrailers] prune delete failed for {Dir}", dir); return false; }
    }

    /// <summary>A bundle is fully cached once its playlist carries EXT-X-ENDLIST.</summary>
    public bool IsComplete(string videoId)
    {
        var path = PlaylistPath(videoId);
        if (!File.Exists(path))
        {
            return false;
        }
        try
        {
            // Playlists are tiny (a few KB); a full read + substring scan for the
            // ENDLIST marker is cheaper than seeking the tail.
            var text = File.ReadAllText(path);
            return text.Contains("#EXT-X-ENDLIST", StringComparison.Ordinal);
        }
        catch
        {
            return false;
        }
    }

    /// <summary>True once init + first segment + playlist exist — enough for AVPlayer to start.</summary>
    public bool IsPlayable(string videoId)
    {
        var dir = BundleDir(videoId);
        return File.Exists(Path.Combine(dir, PlaylistName))
            && File.Exists(Path.Combine(dir, InitName))
            && File.Exists(Path.Combine(dir, "seg0.m4s"));
    }

    /// <summary>
    /// Ensures a remux job for the video ID is running or already complete.
    /// Returns false only when the pipeline can't even be started (bad config,
    /// resolve failure). Does NOT wait for playable output — call
    /// <see cref="WaitForPlayableAsync"/> for that.
    /// </summary>
    public async Task<bool> StartIfNeededAsync(string videoId, CancellationToken cancellationToken)
    {
        if (!IsValidVideoId(videoId))
        {
            _logger.LogWarning("[YouTubeTrailers] Rejected invalid video ID: {VideoId}", videoId);
            return false;
        }
        if (IsComplete(videoId))
        {
            return true;
        }

        var cfg = Plugin.Instance?.Configuration;
        if (cfg is null || !cfg.Enabled)
        {
            return false;
        }

        var gate = _gates.GetOrAdd(videoId, _ => new SemaphoreSlim(1, 1));
        await gate.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            if (IsComplete(videoId))
            {
                return true;
            }
            if (_jobs.TryGetValue(videoId, out var existing) && !existing.Failed && !existing.RunTask.IsCompleted)
            {
                return true; // already building
            }

            // Resolve URLs (fast — ~2-3s), then spawn ffmpeg without awaiting it.
            var urls = await ResolveUrlsAsync(videoId, cfg, cancellationToken).ConfigureAwait(false);
            if (urls is null || urls.Length == 0)
            {
                _logger.LogWarning("[YouTubeTrailers] yt-dlp returned no URLs for {VideoId}", videoId);
                return false;
            }

            var job = await StartRemuxAsync(videoId, urls, cfg, cancellationToken).ConfigureAwait(false);
            if (job is null)
            {
                return false;
            }
            _jobs[videoId] = job;
            return true;
        }
        catch (OperationCanceledException)
        {
            return false;
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "[YouTubeTrailers] StartIfNeeded failed for {VideoId}", videoId);
            return false;
        }
        finally
        {
            gate.Release();
        }
    }

    /// <summary>Waits until the bundle is playable (init+seg0+playlist) or the job dies.</summary>
    public async Task<bool> WaitForPlayableAsync(string videoId, CancellationToken cancellationToken)
    {
        var cfg = Plugin.Instance?.Configuration;
        var timeoutMs = Math.Clamp(cfg?.ResolveTimeoutSeconds ?? 60, 10, 300) * 1000;
        var sw = Stopwatch.StartNew();
        while (sw.ElapsedMilliseconds < timeoutMs)
        {
            if (IsPlayable(videoId))
            {
                return true;
            }
            if (_jobs.TryGetValue(videoId, out var job) && job.Failed)
            {
                return false;
            }
            await Task.Delay(150, cancellationToken).ConfigureAwait(false);
        }
        return IsPlayable(videoId);
    }

    /// <summary>
    /// Waits for a specific segment/init file to appear while its job is still
    /// producing output — covers AVPlayer requesting segN before ffmpeg has
    /// written it. Returns false if the job dies or the wait times out.
    /// </summary>
    public async Task<bool> WaitForFileAsync(string videoId, string fileName, CancellationToken cancellationToken)
    {
        var path = FilePath(videoId, fileName);
        var sw = Stopwatch.StartNew();
        while (sw.ElapsedMilliseconds < 20_000)
        {
            if (File.Exists(path))
            {
                return true;
            }
            // If the job is gone/complete and the file still isn't here, it never will be.
            var jobActive = _jobs.TryGetValue(videoId, out var job) && !job.Failed && !job.RunTask.IsCompleted;
            if (!jobActive)
            {
                return File.Exists(path);
            }
            await Task.Delay(120, cancellationToken).ConfigureAwait(false);
        }
        return File.Exists(path);
    }

    /// <summary>
    /// Splits a configured argument string into individual args, honoring simple
    /// double-quotes so values with spaces (e.g. a cookies path) stay intact.
    /// </summary>
    private static IEnumerable<string> SplitArgs(string? args)
    {
        if (string.IsNullOrWhiteSpace(args))
        {
            yield break;
        }
        var sb = new StringBuilder();
        var inQuotes = false;
        foreach (var c in args)
        {
            if (c == '"')
            {
                inQuotes = !inQuotes;
            }
            else if (char.IsWhiteSpace(c) && !inQuotes)
            {
                if (sb.Length > 0) { yield return sb.ToString(); sb.Clear(); }
            }
            else
            {
                sb.Append(c);
            }
        }
        if (sb.Length > 0) { yield return sb.ToString(); }
    }

    private async Task<string[]?> ResolveUrlsAsync(string videoId, PluginConfiguration cfg, CancellationToken ct)
    {
        var ytDlp = _ytDlp.Resolve();
        if (ytDlp is null)
        {
            _logger.LogError("[YouTubeTrailers] no usable yt-dlp (configured path missing and managed binary not installed)");
            return null;
        }

        var psi = new ProcessStartInfo
        {
            FileName = ytDlp,
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            UseShellExecute = false,
        };
        psi.ArgumentList.Add("-f");
        psi.ArgumentList.Add(cfg.FormatSelector);
        psi.ArgumentList.Add("--get-url");
        psi.ArgumentList.Add("--no-warnings");
        psi.ArgumentList.Add("--no-playlist");
        if (!string.IsNullOrWhiteSpace(cfg.Proxy))
        {
            psi.ArgumentList.Add("--proxy");
            psi.ArgumentList.Add(cfg.Proxy);
        }
        // Admin-configured extra args (cookies, extractor-args, rate limit…).
        foreach (var arg in SplitArgs(cfg.YtDlpArguments))
        {
            psi.ArgumentList.Add(arg);
        }
        psi.ArgumentList.Add($"https://www.youtube.com/watch?v={videoId}");

        using var timeoutCts = CancellationTokenSource.CreateLinkedTokenSource(ct);
        timeoutCts.CancelAfter(TimeSpan.FromSeconds(30));
        var (exit, stdout, stderr) = await RunProcessAsync(psi, timeoutCts.Token).ConfigureAwait(false);
        if (exit != 0)
        {
            _logger.LogWarning("[YouTubeTrailers] yt-dlp exit {Exit} for {VideoId}: {Err}", exit, videoId, stderr.Trim());
            return null;
        }

        var urls = stdout.Split('\n', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries);
        return urls.Length is 1 or 2 ? urls : null;
    }

    /// <summary>
    /// Spawns ffmpeg writing a live event-playlist HLS bundle into the video's
    /// dir and returns immediately with a job whose RunTask completes when
    /// ffmpeg exits. The bundle is built in place; IsComplete (ENDLIST) is the
    /// authoritative "fully cached" signal.
    /// </summary>
    private async Task<TrailerJob?> StartRemuxAsync(string videoId, string[] urls, PluginConfiguration cfg, CancellationToken ct)
    {
        var ffmpeg = ResolveFfmpegPath(cfg);
        if (ffmpeg is null)
        {
            _logger.LogError("[YouTubeTrailers] no usable ffmpeg (configured='{Cfg}', encoder='{Enc}')",
                cfg.FfmpegPath, _mediaEncoder.EncoderPath);
            return null;
        }

        var dir = BundleDir(videoId);
        // Wipe any stale/partial bundle from a previous crashed run, start clean.
        try { if (Directory.Exists(dir)) Directory.Delete(dir, recursive: true); } catch { /* ignore */ }
        Directory.CreateDirectory(dir);

        var psi = new ProcessStartInfo
        {
            FileName = ffmpeg,
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            UseShellExecute = false,
            WorkingDirectory = dir,
        };
        psi.ArgumentList.Add("-loglevel");
        psi.ArgumentList.Add("error");
        psi.ArgumentList.Add("-y");
        foreach (var url in urls)
        {
            // Resilience for servers with a flaky/slow path to googlevideo
            // (connection timeouts surface as ffmpeg ETIMEDOUT, e.g. -138 on
            // Windows): reconnect on network errors and mid-stream drops instead
            // of failing the whole build. These are input options — must precede
            // the matching -i. Harmless on a healthy network (no reconnects fire).
            psi.ArgumentList.Add("-reconnect");
            psi.ArgumentList.Add("1");
            psi.ArgumentList.Add("-reconnect_on_network_error");
            psi.ArgumentList.Add("1");
            psi.ArgumentList.Add("-reconnect_streamed");
            psi.ArgumentList.Add("1");
            psi.ArgumentList.Add("-reconnect_delay_max");
            psi.ArgumentList.Add("5");
            // Proxy the ACTUAL fetch too (not just yt-dlp's resolution) — without
            // this, geo-blocked content resolves through the proxy but ffmpeg
            // still fetches direct and gets blocked.
            if (!string.IsNullOrWhiteSpace(cfg.Proxy))
            {
                psi.ArgumentList.Add("-http_proxy");
                psi.ArgumentList.Add(cfg.Proxy);
            }
            psi.ArgumentList.Add("-i");
            psi.ArgumentList.Add(url);
        }
        if (urls.Length == 2)
        {
            psi.ArgumentList.Add("-map");
            psi.ArgumentList.Add("0:v:0");
            psi.ArgumentList.Add("-map");
            psi.ArgumentList.Add("1:a:0");
        }
        else
        {
            psi.ArgumentList.Add("-map");
            psi.ArgumentList.Add("0");
        }
        psi.ArgumentList.Add("-c");
        psi.ArgumentList.Add("copy");
        psi.ArgumentList.Add("-f");
        psi.ArgumentList.Add("hls");
        psi.ArgumentList.Add("-hls_time");
        psi.ArgumentList.Add("4");
        psi.ArgumentList.Add("-hls_playlist_type");
        psi.ArgumentList.Add("event");
        psi.ArgumentList.Add("-hls_segment_type");
        psi.ArgumentList.Add("fmp4");
        psi.ArgumentList.Add("-hls_fmp4_init_filename");
        psi.ArgumentList.Add(InitName);
        psi.ArgumentList.Add("-hls_segment_filename");
        psi.ArgumentList.Add("seg%d.m4s");
        // temp_file: ffmpeg writes seg.tmp then renames, so a partial segment is
        // never visible to a concurrent segment request.
        psi.ArgumentList.Add("-hls_flags");
        psi.ArgumentList.Add("independent_segments+temp_file");
        psi.ArgumentList.Add(PlaylistName);

        // Belt-and-suspenders proxy: some ffmpeg builds (notably Jellyfin's
        // Windows 7.x with schannel) ignore the -http_proxy *option* for HTTPS
        // but DO honor the proxy environment variables — set both casings so the
        // fetch is reliably routed regardless of build.
        if (!string.IsNullOrWhiteSpace(cfg.Proxy))
        {
            psi.Environment["http_proxy"] = cfg.Proxy;
            psi.Environment["https_proxy"] = cfg.Proxy;
            psi.Environment["HTTP_PROXY"] = cfg.Proxy;
            psi.Environment["HTTPS_PROXY"] = cfg.Proxy;
        }

        // Bound concurrent ffmpeg jobs. Acquired here, released when ffmpeg exits.
        // Cancellable so a request abandoned while queued for a slot (and holding
        // the per-video gate) doesn't pin it for the slot-holder's full build.
        await _startSlots.WaitAsync(ct).ConfigureAwait(false);

        var process = new Process { StartInfo = psi, EnableRaisingEvents = true };
        var stderr = new StringBuilder();
        process.ErrorDataReceived += (_, e) => { if (e.Data is not null) stderr.AppendLine(e.Data); };

        var startedUtc = DateTime.UtcNow;
        var job = new TrailerJob(process, startedUtc);

        try
        {
            process.Start();
            process.BeginErrorReadLine();
        }
        catch (Exception ex)
        {
            _startSlots.Release();
            _logger.LogError(ex, "[YouTubeTrailers] failed to start ffmpeg for {VideoId}", videoId);
            return null;
        }

        // Monitor exit asynchronously — sets Failed and releases the slot.
        job.RunTask = Task.Run(async () =>
        {
            try
            {
                await process.WaitForExitAsync().ConfigureAwait(false);
                if (process.ExitCode != 0 || !IsComplete(videoId))
                {
                    job.Failed = true;
                    _logger.LogWarning("[YouTubeTrailers] ffmpeg exit {Exit} for {VideoId}: {Err}",
                        process.ExitCode, videoId, stderr.ToString().Trim());
                }
                else
                {
                    _logger.LogInformation(
                        "[YouTubeTrailers] Completed bundle for {VideoId} in {Ms}ms ({Mode})",
                        videoId, (long)(DateTime.UtcNow - startedUtc).TotalMilliseconds,
                        urls.Length == 2 ? "adaptive" : "muxed");
                }
            }
            catch (Exception ex)
            {
                job.Failed = true;
                _logger.LogError(ex, "[YouTubeTrailers] ffmpeg monitor failed for {VideoId}", videoId);
            }
            finally
            {
                _startSlots.Release();
                try { process.Dispose(); } catch { /* ignore */ }
                // Evict ourselves so _jobs only ever holds in-flight builds.
                // KeyValuePair overload removes only if THIS job is still mapped
                // (a newer rebuild for the same id is left intact). Removing a
                // failed job is what lets the next request retry from scratch.
                _jobs.TryRemove(new KeyValuePair<string, TrailerJob>(videoId, job));
            }
        });

        _logger.LogInformation("[YouTubeTrailers] Started remux for {VideoId} ({Mode})",
            videoId, urls.Length == 2 ? "adaptive" : "muxed");
        return job;
    }

    /// <summary>
    /// Resolves a usable ffmpeg. Jellyfin's EncoderPath is often the bare name
    /// "ffmpeg" (resolved via PATH at launch), which File.Exists can't validate
    /// — so prefer absolute candidates that exist, falling back to a bare name
    /// (Process resolves it via PATH) only as a last resort.
    /// </summary>
    private string? ResolveFfmpegPath(PluginConfiguration cfg)
    {
        string?[] candidates =
        {
            string.IsNullOrWhiteSpace(cfg.FfmpegPath) ? null : cfg.FfmpegPath,
            _mediaEncoder.EncoderPath,
            "/opt/homebrew/bin/ffmpeg",
            "/usr/local/bin/ffmpeg",
            "/usr/bin/ffmpeg",
        };
        foreach (var c in candidates)
        {
            if (!string.IsNullOrWhiteSpace(c) && c.Contains('/') && File.Exists(c))
            {
                return c;
            }
        }
        foreach (var c in candidates)
        {
            if (!string.IsNullOrWhiteSpace(c))
            {
                return c;
            }
        }
        return null;
    }

    private static async Task<(int Exit, string Stdout, string Stderr)> RunProcessAsync(
        ProcessStartInfo psi, CancellationToken ct)
    {
        using var process = new Process { StartInfo = psi, EnableRaisingEvents = true };
        var stdout = new StringBuilder();
        var stderr = new StringBuilder();
        process.OutputDataReceived += (_, e) => { if (e.Data is not null) stdout.AppendLine(e.Data); };
        process.ErrorDataReceived += (_, e) => { if (e.Data is not null) stderr.AppendLine(e.Data); };

        process.Start();
        process.BeginOutputReadLine();
        process.BeginErrorReadLine();
        try
        {
            await process.WaitForExitAsync(ct).ConfigureAwait(false);
        }
        catch (OperationCanceledException)
        {
            try { if (!process.HasExited) process.Kill(entireProcessTree: true); } catch { /* best effort */ }
            throw;
        }
        return (process.ExitCode, stdout.ToString(), stderr.ToString());
    }

    /// <summary>Rewrites init/segment URIs in a served playlist to carry the caller's auth token.</summary>
    public static string InjectAuth(string playlist, string token)
    {
        if (string.IsNullOrEmpty(token))
        {
            return playlist;
        }
        playlist = playlist.Replace($"URI=\"{InitName}\"", $"URI=\"{InitName}?api_key={token}\"", StringComparison.Ordinal);
        playlist = Regex.Replace(playlist, @"(?m)^(seg\d+\.m4s)$", $"$1?api_key={token}");
        return playlist;
    }

    public sealed class TrailerJob
    {
        public TrailerJob(Process process, DateTime startedUtc)
        {
            Process = process;
            StartedUtc = startedUtc;
            RunTask = Task.CompletedTask;
        }

        public Process Process { get; }
        public DateTime StartedUtc { get; }
        public Task RunTask { get; set; }
        public volatile bool Failed;
    }
}
