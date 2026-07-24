using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.Diagnostics;
using System.Globalization;
using System.IO;
using System.Linq;
using System.Text.Json;
using System.Text.RegularExpressions;
using System.Threading;
using System.Threading.Tasks;
using MediaBrowser.Common.Configuration;
using Microsoft.Extensions.Logging;

namespace Jellyfin.Plugin.YouTubeTrailers.Services;

/// <summary>
/// Resolves a YouTube video ID to an AVPlayer-native fMP4 HLS bundle via
/// yt-dlp (URL resolution) + ffmpeg (stream-copy remux). Uses a LIVE event
/// playlist: ffmpeg writes segments incrementally and the server serves
/// segment 0 the moment it exists, so time-to-first-frame is ~resolve + one
/// segment regardless of trailer length, and googlevideo's ~1x read throttle
/// stops mattering (AVPlayer consumes at 1x while ffmpeg stays ahead).
///
/// Every build is tracked as a <see cref="TrailerJob"/> from the instant it is
/// requested — through resolve, the concurrency queue, and the remux itself —
/// so the dashboard can show live progress and cancel work, and so a failure
/// lands in the diagnostics log with the phase and tool output that explain it.
/// </summary>
public sealed class TrailerResolver
{
    // YouTube IDs are exactly 11 chars of [A-Za-z0-9_-]. Validating up front
    // is both correctness and the injection guard for the shell-out below.
    private static readonly Regex VideoIdPattern = new("^[A-Za-z0-9_-]{11}$", RegexOptions.Compiled);
    private const string InitName = "init.mp4";
    private const string PlaylistName = "main.m3u8";
    private const string SidecarName = "bundle.json";
    // Zero-byte file whose mtime is the bundle's last-played time. A separate
    // marker (rather than touching the playlist) keeps "built" and "last used"
    // independent in both the dashboard and the pruner.
    private const string AccessMarkerName = "accessed";
    private static readonly TimeSpan TouchThrottle = TimeSpan.FromHours(1);
    private const int MaxHistory = 100;

    private readonly ILogger<TrailerResolver> _logger;
    private readonly IApplicationPaths _appPaths;
    private readonly YtDlpManager _ytDlp;
    private readonly ToolRunner _tools;
    private readonly TrailerDiagnostics _diagnostics;

    private readonly ConcurrentDictionary<string, SemaphoreSlim> _gates = new();
    private readonly ConcurrentDictionary<string, TrailerJob> _jobs = new();
    private readonly ConcurrentDictionary<string, DateTime> _lastTouch = new();
    // Short-lived record of admin cancellations. A client already blocked in
    // WaitForPlayableAsync has no other way to learn the build went away — the
    // job is evicted from _jobs and a cancel deliberately writes no negative-
    // cache entry — so without this it would sit out the full resolve timeout.
    private readonly ConcurrentDictionary<string, DateTime> _recentCancels = new();
    private static readonly TimeSpan CancelSignalTtl = TimeSpan.FromSeconds(30);
    // Negative cache: a video that just failed to build (unavailable, geo-blocked,
    // or an unreachable CDN edge) fast-fails for a short window instead of
    // re-running the full timeout on every request. This is what makes the
    // client's "try the next trailer" fallback quick on repeat/prewarmed plays.
    private readonly ConcurrentDictionary<string, FailureMark> _recentFailures = new();
    // Bounded ring of finished builds so the dashboard can show what just
    // happened, not only what is happening right now.
    private readonly object _historySync = new();
    private readonly LinkedList<JobHistoryEntry> _history = new();
    // Caps concurrent ffmpeg remuxes so prewarming a shelf can't spawn a swarm.
    // Built at the hard ceiling (MaxPossibleBuilds) and then trimmed down to the
    // configured value, so the pool can be resized live in either direction when
    // the admin saves — see ApplyConcurrency. Releasing beyond the constructed
    // maxCount would throw, which is why the ceiling (not the current setting)
    // is what the semaphore is constructed with.
    private const int MaxPossibleBuilds = 16;
    private readonly SemaphoreSlim _startSlots = new(MaxPossibleBuilds, MaxPossibleBuilds);
    private readonly object _concurrencySync = new();
    private int _grantedSlots = MaxPossibleBuilds;

    public TrailerResolver(
        ILogger<TrailerResolver> logger,
        IApplicationPaths appPaths,
        YtDlpManager ytDlp,
        ToolRunner tools,
        TrailerDiagnostics diagnostics)
    {
        _logger = logger;
        _appPaths = appPaths;
        _ytDlp = ytDlp;
        _tools = tools;
        _diagnostics = diagnostics;
        ApplyConcurrency();
        if (Plugin.Instance is not null)
        {
            // Resize the build pool the moment settings are saved, so changing
            // it doesn't require a server restart.
            Plugin.Instance.ConfigurationChanged += (_, _) => ApplyConcurrency();
        }
    }

    public static bool IsValidVideoId(string videoId) => VideoIdPattern.IsMatch(videoId);

    public int MaxConcurrentBuilds =>
        Math.Clamp(Config?.MaxConcurrentBuilds ?? 4, 1, MaxPossibleBuilds);

    /// <summary>
    /// Resizes the build-slot pool to match the configured concurrency.
    ///
    /// Growing is a plain <c>Release</c>. Shrinking can't revoke permits that
    /// are already held, so it *absorbs* the difference: a background waiter
    /// takes the surplus permits and never gives them back. In-flight builds are
    /// therefore never interrupted — the new, lower ceiling simply takes effect
    /// as running builds finish.
    /// </summary>
    private void ApplyConcurrency()
    {
        var target = MaxConcurrentBuilds;
        lock (_concurrencySync)
        {
            var delta = target - _grantedSlots;
            if (delta == 0)
            {
                return;
            }
            _grantedSlots = target;
            if (delta > 0)
            {
                _startSlots.Release(delta);
            }
            else
            {
                var toAbsorb = -delta;
                _ = Task.Run(async () =>
                {
                    for (var i = 0; i < toAbsorb; i++)
                    {
                        try { await _startSlots.WaitAsync().ConfigureAwait(false); }
                        catch (ObjectDisposedException) { return; }
                    }
                });
            }
            _logger.LogInformation("[YouTubeTrailers] Build concurrency set to {Target}", target);
        }
    }

    private static PluginConfiguration? Config => Plugin.Instance?.Configuration;

    private static TimeSpan FailureTtl =>
        TimeSpan.FromMinutes(Math.Clamp(Config?.FailureCacheMinutes ?? 10, 0, 1440));

    private string CacheRoot
    {
        get
        {
            var cfg = Config;
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

    /// <summary>
    /// One row per cached bundle for the dashboard's cache browser: size,
    /// segment count, whether the remux finished, and the YouTube title from
    /// the sidecar so the list is readable without cross-referencing IDs.
    /// </summary>
    public IReadOnlyList<BundleInfo> ListBundles()
    {
        var root = CacheRoot;
        var rows = new List<BundleInfo>();
        if (!Directory.Exists(root))
        {
            return rows;
        }
        foreach (var dir in Directory.EnumerateDirectories(root))
        {
            var id = Path.GetFileName(dir);
            if (!IsValidVideoId(id))
            {
                continue;
            }
            long bytes = 0;
            var segments = 0;
            var built = DateTime.MinValue;
            try
            {
                foreach (var file in Directory.EnumerateFiles(dir, "*", SearchOption.TopDirectoryOnly))
                {
                    var fi = new FileInfo(file);
                    bytes += fi.Length;
                    // The access marker is deliberately excluded from "built":
                    // serving a trailer touches it, and letting that bump the
                    // build time would make every played trailer look freshly
                    // built in the dashboard.
                    if (fi.Name.Equals(AccessMarkerName, StringComparison.Ordinal)) continue;
                    if (fi.LastWriteTimeUtc > built) built = fi.LastWriteTimeUtc;
                    if (fi.Name.EndsWith(".m4s", StringComparison.Ordinal)) segments++;
                }
            }
            catch (IOException) { /* racing prune */ }
            catch (UnauthorizedAccessException) { /* unreadable */ }

            var sidecar = ReadSidecar(dir);
            rows.Add(new BundleInfo(
                id,
                sidecar?.Title,
                bytes,
                segments,
                IsComplete(id),
                built,
                LastUsedUtc(dir, built),
                sidecar?.DurationSeconds,
                IsBuilding(id)));
        }
        return rows;
    }

    /// <summary>Deletes one cached bundle. Refuses while a build is writing into it.</summary>
    public bool DeleteBundle(string videoId)
    {
        if (!IsValidVideoId(videoId) || IsBuilding(videoId))
        {
            return false;
        }
        var dir = BundleDir(videoId);
        return Directory.Exists(dir) && TryDeleteDir(dir);
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
            var built = DateTime.MinValue;
            foreach (var file in Directory.EnumerateFiles(dir, "*", SearchOption.AllDirectories))
            {
                try
                {
                    var fi = new FileInfo(file);
                    size += fi.Length;
                    if (fi.Name.Equals(AccessMarkerName, StringComparison.Ordinal)) continue;
                    if (fi.LastWriteTimeUtc > built) built = fi.LastWriteTimeUtc;
                }
                catch { /* racing */ }
            }
            // Evict on last USE, not last build. Without the access marker a
            // trailer played every single day would still be evicted on its
            // 30th day exactly like one nobody ever watched — which is the
            // opposite of the "least recently used" behaviour advertised.
            bundles.Add((dir, id, LastUsedUtc(dir, built), size));
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
    /// Installed yt-dlp version for the dashboard, or a sentence explaining why
    /// the probe failed. Never returns a bare "error": a stale binary, a
    /// wrong-arch download and a slow first start all look identical under that
    /// label but need completely different fixes.
    /// </summary>
    public async Task<string> YtDlpVersionAsync(CancellationToken ct)
    {
        var ytDlp = _ytDlp.Resolve();
        if (ytDlp is null)
        {
            return "not installed";
        }
        var probe = await ToolRunner
            .ProbeVersionAsync(ytDlp, ["--version"], TimeSpan.FromSeconds(30), ct)
            .ConfigureAwait(false);
        if (!probe.Ok)
        {
            _logger.LogWarning("[YouTubeTrailers] yt-dlp version probe failed: {Detail}", probe.Detail);
        }
        return probe.Version ?? probe.Detail;
    }

    /// <summary>
    /// Records that a bundle was just played, so age/LRU eviction reflects real
    /// usage. Throttled to once an hour per bundle — a single playback is dozens
    /// of segment requests and rewriting the marker for each would be pointless
    /// disk churn.
    /// </summary>
    public void TouchAccess(string videoId)
    {
        if (!IsValidVideoId(videoId))
        {
            return;
        }
        var now = DateTime.UtcNow;
        if (_lastTouch.TryGetValue(videoId, out var previous) && now - previous < TouchThrottle)
        {
            return;
        }
        _lastTouch[videoId] = now;
        try
        {
            var dir = BundleDir(videoId);
            if (!Directory.Exists(dir))
            {
                return;
            }
            var marker = Path.Combine(dir, AccessMarkerName);
            if (File.Exists(marker))
            {
                File.SetLastWriteTimeUtc(marker, now);
            }
            else
            {
                File.WriteAllBytes(marker, []);
            }
        }
        catch (Exception ex)
        {
            _logger.LogDebug(ex, "[YouTubeTrailers] could not record access for {VideoId}", videoId);
        }
    }

    /// <summary>Last-played time from the access marker, falling back to build time for bundles that predate it.</summary>
    private static DateTime LastUsedUtc(string dir, DateTime built)
    {
        try
        {
            var marker = new FileInfo(Path.Combine(dir, AccessMarkerName));
            if (marker.Exists && marker.LastWriteTimeUtc > built)
            {
                return marker.LastWriteTimeUtc;
            }
        }
        catch { /* unreadable — fall back */ }
        return built;
    }

    public bool IsBuilding(string videoId) =>
        _jobs.TryGetValue(videoId, out var job) && !job.IsFinished;

    private bool TryDeleteDir(string dir)
    {
        try { Directory.Delete(dir, recursive: true); return true; }
        catch (Exception ex) { _logger.LogDebug(ex, "[YouTubeTrailers] delete failed for {Dir}", dir); return false; }
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

    // ---- Job inspection / control (dashboard) -----------------------------

    /// <summary>Live snapshot of every build currently resolving, queued, or remuxing.</summary>
    public IReadOnlyList<JobSnapshot> ActiveJobs() =>
        _jobs.Values.Where(j => !j.IsFinished).Select(Snapshot)
            .OrderBy(j => j.Phase == "building" ? 0 : j.Phase == "resolving" ? 1 : 2)
            .ThenBy(j => j.StartedUtc)
            .ToList();

    /// <summary>Most-recently-finished builds, newest first.</summary>
    public IReadOnlyList<JobHistoryEntry> History(int limit = 25)
    {
        lock (_historySync)
        {
            return _history.Take(Math.Clamp(limit, 1, MaxHistory)).ToList();
        }
    }

    /// <summary>Video IDs currently fast-failing, with the moment they become retryable again.</summary>
    public IReadOnlyList<NegativeCacheEntry> NegativeCache()
    {
        var ttl = FailureTtl;
        return _recentFailures
            .Select(kv => new NegativeCacheEntry(kv.Key, kv.Value.WhenUtc, kv.Value.WhenUtc + ttl, kv.Value.Reason))
            .OrderByDescending(e => e.FailedUtc)
            .ToList();
    }

    /// <summary>Drops all negative-cache entries so blocked videos are retried immediately.</summary>
    public int ClearNegativeCache()
    {
        var n = _recentFailures.Count;
        _recentFailures.Clear();
        return n;
    }

    public bool ClearNegativeCache(string videoId) => _recentFailures.TryRemove(videoId, out _);

    /// <summary>
    /// Cancels an in-flight build: kills ffmpeg (or aborts the resolve), drops
    /// the partial bundle, and records the job as canceled. A previously
    /// completed bundle for the same ID is untouched — cancel only ever throws
    /// away work that was still in progress.
    /// </summary>
    public bool TryCancel(string videoId)
    {
        if (!_jobs.TryGetValue(videoId, out var job) || job.IsFinished)
        {
            return false;
        }
        job.Canceled = true;
        try { job.Cts.Cancel(); } catch (ObjectDisposedException) { /* already torn down */ }
        var process = job.Process;
        if (process is not null)
        {
            try { if (!process.HasExited) process.Kill(entireProcessTree: true); } catch { /* already gone */ }
        }
        _logger.LogInformation("[YouTubeTrailers] Canceled build for {VideoId}", videoId);
        return true;
    }

    public int CancelAll()
    {
        var canceled = 0;
        foreach (var id in _jobs.Keys.ToList())
        {
            if (TryCancel(id)) canceled++;
        }
        return canceled;
    }

    private JobSnapshot Snapshot(TrailerJob job)
    {
        double? percent = null;
        if (job.DurationSeconds is > 0)
        {
            var seconds = Interlocked.Read(ref job.OutTimeUs) / 1_000_000d;
            percent = Math.Clamp(seconds / job.DurationSeconds.Value * 100d, 0, 100);
        }
        var (segments, bytes) = MeasureBundle(job.VideoId);
        return new JobSnapshot(
            job.VideoId,
            job.Label,
            job.Title,
            job.Source,
            job.Phase,
            job.StartedUtc,
            job.DurationSeconds,
            percent,
            job.Speed > 0 ? job.Speed : null,
            bytes,
            segments,
            IsPlayable(job.VideoId));
    }

    /// <summary>
    /// Segment count and bytes written so far, measured on disk.
    ///
    /// ffmpeg's <c>-progress</c> reports <c>total_size=N/A</c> for the HLS muxer
    /// (it tracks the primary output IO context, and HLS writes each segment to
    /// its own file), so the bundle directory is the only honest source for
    /// "how much has been produced". out_time and speed from -progress are still
    /// accurate and drive the percentage.
    /// </summary>
    private (int Segments, long Bytes) MeasureBundle(string videoId)
    {
        try
        {
            var dir = BundleDir(videoId);
            if (!Directory.Exists(dir))
            {
                return (0, 0);
            }
            var segments = 0;
            long bytes = 0;
            foreach (var file in Directory.EnumerateFiles(dir, "*", SearchOption.TopDirectoryOnly))
            {
                if (file.EndsWith(".m4s", StringComparison.Ordinal))
                {
                    segments++;
                }
                try { bytes += new FileInfo(file).Length; } catch { /* mid-rename */ }
            }
            return (segments, bytes);
        }
        catch
        {
            return (0, 0);
        }
    }

    private void PushHistory(JobHistoryEntry entry)
    {
        lock (_historySync)
        {
            _history.AddFirst(entry);
            while (_history.Count > MaxHistory)
            {
                _history.RemoveLast();
            }
        }
    }

    // ---- Build pipeline ---------------------------------------------------

    /// <summary>
    /// Ensures a remux job for the video ID is running or already complete.
    /// Returns false only when the pipeline can't even be started (bad config,
    /// resolve failure). Does NOT wait for playable output — call
    /// <see cref="WaitForPlayableAsync"/> for that.
    /// </summary>
    public async Task<bool> StartIfNeededAsync(
        string videoId,
        CancellationToken cancellationToken,
        string source = "playback",
        string? label = null)
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

        var cfg = Config;
        if (cfg is null || !cfg.Enabled)
        {
            return false;
        }

        // Negative-cache fast path: if this video failed recently, don't spend
        // another full timeout — fail immediately so the client moves on to the
        // next candidate. Stale entries fall through to a fresh attempt.
        if (_recentFailures.TryGetValue(videoId, out var mark))
        {
            if (DateTime.UtcNow - mark.WhenUtc < FailureTtl)
            {
                return false;
            }
            _recentFailures.TryRemove(videoId, out _);
        }

        var gate = _gates.GetOrAdd(videoId, _ => new SemaphoreSlim(1, 1));
        await gate.WaitAsync(cancellationToken).ConfigureAwait(false);
        TrailerJob? job = null;
        try
        {
            if (IsComplete(videoId))
            {
                return true;
            }
            if (_jobs.TryGetValue(videoId, out var existing) && !existing.IsFinished)
            {
                // Already building. Adopt a better label if this caller has one
                // (a library-add knows the movie title; a raw playback request
                // does not), so the dashboard row becomes readable.
                existing.Label ??= label;
                return true;
            }

            job = new TrailerJob(videoId, source, label);
            _jobs[videoId] = job;
            _recentCancels.TryRemove(videoId, out _); // a fresh attempt supersedes any earlier cancel

            // Resolve URLs (fast — ~2-3s), then spawn ffmpeg without awaiting it.
            job.Phase = JobPhases.Resolving;
            using var resolveCts = CancellationTokenSource
                .CreateLinkedTokenSource(cancellationToken, job.Cts.Token);
            var resolved = await _tools.ResolveVideoAsync(videoId, resolveCts.Token).ConfigureAwait(false);
            if (!resolved.Ok)
            {
                var reason = resolved.TimedOut
                    ? "yt-dlp timed out (30s) — this server can't reach YouTube, or a configured proxy is unreachable."
                    : resolved.Urls.Length == 0
                        ? "yt-dlp resolved no stream URLs (video unavailable/geo-blocked, or no format matched the selector)."
                        : $"yt-dlp returned {resolved.Urls.Length} URLs; expected 1 or 2.";
                FailJob(job, JobPhases.Resolving, reason, resolved.Exit,
                    ToolRunner.TailLines(resolved.Stderr), resolved.Command);
                return false;
            }

            job.Title = resolved.Title;
            job.DurationSeconds = resolved.DurationSeconds;

            var started = await StartRemuxAsync(job, resolved, cfg, cancellationToken).ConfigureAwait(false);
            return started;
        }
        catch (OperationCanceledException)
        {
            if (job is not null)
            {
                CancelJob(job, "Request canceled before the build started.");
            }
            return false;
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "[YouTubeTrailers] StartIfNeeded failed for {VideoId}", videoId);
            if (job is not null)
            {
                FailJob(job, job.Phase, ex.Message, null, string.Empty, string.Empty);
            }
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
        var timeoutMs = Math.Clamp(Config?.ResolveTimeoutSeconds ?? 60, 10, 300) * 1000;
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
            // The monitor sets job.Failed then immediately EVICTS the job from
            // _jobs (so a retry starts fresh), so the check above is easily missed
            // — the client would then wait out the full timeout even though the
            // build (or the watchdog kill) already failed. The negative-cache
            // entry is written at the same instant and survives eviction, so treat
            // it as the authoritative "this build failed" signal. (This is what
            // makes the watchdog's kill actually reach the client.)
            if (_recentFailures.ContainsKey(videoId))
            {
                return false;
            }
            // An admin cancelling a build must reach the client immediately
            // rather than leaving it to time out.
            if (_recentCancels.TryGetValue(videoId, out var canceledAt))
            {
                if (DateTime.UtcNow - canceledAt < CancelSignalTtl)
                {
                    return false;
                }
                _recentCancels.TryRemove(videoId, out _);
            }
            await Task.Delay(150, cancellationToken).ConfigureAwait(false);
        }
        return IsPlayable(videoId);
    }

    /// <summary>
    /// Waits (bounded) until the bundle is fully remuxed (ENDLIST) or the job
    /// dies. Used by the full-screen client path so AVKit loads a finite VOD
    /// playlist (a real scrubber) instead of the live-stream UI. On timeout the
    /// caller falls back to the still-live playlist, so a slow remux never blocks
    /// playback for long.
    /// </summary>
    public async Task<bool> WaitForCompleteAsync(string videoId, CancellationToken cancellationToken)
    {
        // Full-screen (?complete=1) grace: serve the finite VOD playlist if the
        // remux is already done (warm/prewarmed → real scrubber, zero wait),
        // otherwise start live almost immediately. Kept tiny so it never adds
        // meaningful startup latency.
        var capMs = Math.Clamp(Config?.CompleteWaitSeconds ?? 3, 0, 60) * 1000;
        var sw = Stopwatch.StartNew();
        while (sw.ElapsedMilliseconds < capMs)
        {
            if (IsComplete(videoId))
            {
                return true;
            }
            if (_jobs.TryGetValue(videoId, out var job) && job.Failed)
            {
                return false;
            }
            await Task.Delay(150, cancellationToken).ConfigureAwait(false);
        }
        return IsComplete(videoId);
    }

    /// <summary>
    /// Once the bundle is finished (ENDLIST present), marks the playlist VOD
    /// instead of EVENT so AVKit renders a normal seek bar rather than the
    /// live-stream UI (red "LIVE" badge + wall-clock). No-op while still live.
    /// </summary>
    public static string FinalizePlaylistType(string playlist)
    {
        if (!playlist.Contains("#EXT-X-ENDLIST", StringComparison.Ordinal))
        {
            return playlist;
        }
        return playlist.Replace(
            "#EXT-X-PLAYLIST-TYPE:EVENT", "#EXT-X-PLAYLIST-TYPE:VOD", StringComparison.Ordinal);
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
            var jobActive = _jobs.TryGetValue(videoId, out var job) && !job.Failed && !job.IsFinished;
            if (!jobActive)
            {
                return File.Exists(path);
            }
            await Task.Delay(120, cancellationToken).ConfigureAwait(false);
        }
        return File.Exists(path);
    }

    /// <summary>
    /// Spawns ffmpeg writing a live event-playlist HLS bundle into the video's
    /// dir and returns as soon as the process is running; the job's RunTask
    /// completes when ffmpeg exits. The bundle is built in place; IsComplete
    /// (ENDLIST) is the authoritative "fully cached" signal.
    /// </summary>
    private async Task<bool> StartRemuxAsync(
        TrailerJob job, ToolRunner.ResolvedVideo resolved, PluginConfiguration cfg, CancellationToken ct)
    {
        var videoId = job.VideoId;
        var urls = resolved.Urls;

        var ffmpeg = _tools.ResolveFfmpeg();
        if (ffmpeg is null)
        {
            FailJob(job, JobPhases.Building,
                $"No usable ffmpeg (configured='{cfg.FfmpegPath}', Jellyfin encoder='{_tools.EncoderPath}').",
                null, string.Empty, string.Empty);
            return false;
        }

        var dir = BundleDir(videoId);
        // Wipe any stale/partial bundle from a previous crashed run, start clean.
        try { if (Directory.Exists(dir)) Directory.Delete(dir, recursive: true); } catch { /* ignore */ }
        Directory.CreateDirectory(dir);
        WriteSidecar(dir, new BundleSidecar(videoId, job.Title, job.DurationSeconds, DateTime.UtcNow, job.Label, job.Source));

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
        // Machine-readable progress on stdout (out_time_us / total_size / speed)
        // is what drives the dashboard's live percentage. -nostats suppresses the
        // human stats line so stderr stays pure error output for the failure log.
        psi.ArgumentList.Add("-nostats");
        psi.ArgumentList.Add("-progress");
        psi.ArgumentList.Add("pipe:1");
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
            // Optional bandwidth cap, expressed as a multiple of realtime. This
            // belongs on ffmpeg, not yt-dlp: ffmpeg performs the actual download
            // (yt-dlp only resolves the URL), so yt-dlp's --limit-rate would have
            // no effect at all. Refuse anything below 1x — the build would then
            // fall behind playback and the no-segment watchdog would kill it.
            if (cfg.BuildSpeedLimit >= 1)
            {
                psi.ArgumentList.Add("-readrate");
                psi.ArgumentList.Add(cfg.BuildSpeedLimit.ToString("0.##", CultureInfo.InvariantCulture));
            }
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

        var command = ToolRunner.Describe(psi);
        job.Command = command;
        if (cfg.VerboseLogging)
        {
            _logger.LogInformation("[YouTubeTrailers] remux command for {VideoId}: {Command}", videoId, command);
        }

        // Bound concurrent ffmpeg jobs. Acquired here, released when ffmpeg exits.
        // Cancellable so a request abandoned while queued for a slot (and holding
        // the per-video gate) doesn't pin it for the slot-holder's full build.
        job.Phase = JobPhases.Queued;
        using (var slotCts = CancellationTokenSource.CreateLinkedTokenSource(ct, job.Cts.Token))
        {
            try
            {
                await _startSlots.WaitAsync(slotCts.Token).ConfigureAwait(false);
            }
            catch (OperationCanceledException)
            {
                CancelJob(job, "Canceled while queued for a build slot.");
                return false;
            }
        }
        job.Phase = JobPhases.Building;
        job.BuildStartedUtc = DateTime.UtcNow;

        var process = new Process { StartInfo = psi, EnableRaisingEvents = true };
        var stderr = new System.Text.StringBuilder();
        process.ErrorDataReceived += (_, e) => { if (e.Data is not null) stderr.AppendLine(e.Data); };
        // -progress emits repeating key=value blocks; we only care about three
        // keys. Reading this stream is also mandatory now that stdout carries
        // data — an unread pipe would eventually block ffmpeg.
        process.OutputDataReceived += (_, e) => ApplyProgressLine(job, e.Data);
        job.Process = process;

        try
        {
            process.Start();
            process.BeginErrorReadLine();
            process.BeginOutputReadLine();
        }
        catch (Exception ex)
        {
            _startSlots.Release();
            FailJob(job, JobPhases.Building, $"Failed to start ffmpeg: {ex.Message}", null, string.Empty, command);
            return false;
        }

        StartNoProgressWatchdog(job, process);

        // Monitor exit asynchronously — records the outcome and releases the slot.
        job.RunTask = Task.Run(async () =>
        {
            try
            {
                await process.WaitForExitAsync().ConfigureAwait(false);
                var elapsedMs = (long)(DateTime.UtcNow - job.StartedUtc).TotalMilliseconds;

                if (job.Canceled)
                {
                    // A canceled build leaves a half-written bundle that would
                    // otherwise look like a legitimate partial cache entry.
                    TryDeleteDir(BundleDir(videoId));
                    CancelJob(job, job.Error ?? "Canceled by an administrator.");
                }
                else if (process.ExitCode != 0 || !IsComplete(videoId))
                {
                    var tail = ToolRunner.TailLines(stderr.ToString());
                    var reason = job.WatchdogKilled
                        ? $"No first segment within {NoSegmentTimeoutSeconds}s — build killed. The CDN edge accepted the "
                          + "connection but never delivered data; ffmpeg's reconnect would retry forever."
                        : process.ExitCode != 0
                            ? $"ffmpeg exited {process.ExitCode} after {elapsedMs} ms."
                            : "ffmpeg exited 0 but the playlist has no EXT-X-ENDLIST — the remux stopped early.";
                    FailJob(job, JobPhases.Building, reason, process.ExitCode, tail, command);
                }
                else
                {
                    _recentFailures.TryRemove(videoId, out _); // recovered — clear any prior failure
                    job.Phase = JobPhases.Completed;
                    job.EndedUtc = DateTime.UtcNow;
                    var final = MeasureBundle(videoId);
                    PushHistory(new JobHistoryEntry(
                        videoId, job.Label, job.Title, job.Source, "completed",
                        job.StartedUtc, job.EndedUtc.Value, elapsedMs,
                        final.Bytes, final.Segments, null, null, null));
                    _logger.LogInformation(
                        "[YouTubeTrailers] Completed bundle for {VideoId} in {Ms}ms ({Mode})",
                        videoId, elapsedMs, urls.Length == 2 ? "adaptive" : "muxed");
                }
            }
            catch (Exception ex)
            {
                FailJob(job, JobPhases.Building, $"Build monitor failed: {ex.Message}", null, string.Empty, command);
            }
            finally
            {
                _startSlots.Release();
                try { process.Dispose(); } catch { /* ignore */ }
                job.Process = null;
                try { job.Cts.Dispose(); } catch { /* ignore */ }
                // Evict ourselves so _jobs only ever holds in-flight builds.
                // KeyValuePair overload removes only if THIS job is still mapped
                // (a newer rebuild for the same id is left intact). Removing a
                // failed job is what lets the next request retry from scratch.
                _jobs.TryRemove(new KeyValuePair<string, TrailerJob>(videoId, job));
            }
        });

        _logger.LogInformation("[YouTubeTrailers] Started remux for {VideoId} ({Mode}) — {Label}",
            videoId, urls.Length == 2 ? "adaptive" : "muxed", job.Label ?? job.Title ?? "unlabelled");
        return true;
    }

    /// <summary>
    /// If no first segment appears within the configured window, the build is
    /// killed and negative-cached. A dead CDN edge makes ffmpeg's -reconnect
    /// retry forever, producing no segment and never exiting on its own — so the
    /// playable-wait would otherwise burn the full ResolveTimeoutSeconds.
    /// Killing early lets the client's fallback chain reach the next trailer fast.
    /// </summary>
    private static int NoSegmentTimeoutSeconds =>
        Math.Clamp(Config?.NoSegmentTimeoutSeconds ?? 20, 5, 120);

    private void StartNoProgressWatchdog(TrailerJob job, Process process)
    {
        _ = Task.Run(async () =>
        {
            try
            {
                await Task.Delay(TimeSpan.FromSeconds(NoSegmentTimeoutSeconds)).ConfigureAwait(false);
                if (!IsPlayable(job.VideoId) && !process.HasExited && !job.Canceled)
                {
                    job.WatchdogKilled = true;
                    _logger.LogWarning(
                        "[YouTubeTrailers] No segment within {Sec}s for {VideoId} — killing stalled build",
                        NoSegmentTimeoutSeconds, job.VideoId);
                    try { process.Kill(entireProcessTree: true); } catch { /* already gone */ }
                }
            }
            catch { /* ignore */ }
        });
    }

    /// <summary>
    /// Consumes one line of ffmpeg's <c>-progress</c> stream. Only three keys
    /// matter: how far into the source we are, how much we've written, and how
    /// fast relative to realtime (a speed well under 1x on a stream copy means
    /// the CDN is throttling, which is exactly what an admin wants to see).
    /// </summary>
    private static void ApplyProgressLine(TrailerJob job, string? line)
    {
        if (string.IsNullOrEmpty(line))
        {
            return;
        }
        var eq = line.IndexOf('=', StringComparison.Ordinal);
        if (eq <= 0)
        {
            return;
        }
        var key = line[..eq].Trim();
        var value = line[(eq + 1)..].Trim();
        switch (key)
        {
            // out_time_us and out_time_ms are BOTH microseconds in ffmpeg's
            // progress output (a long-standing naming bug); either is fine.
            case "out_time_us":
            case "out_time_ms":
                if (long.TryParse(value, NumberStyles.Integer, CultureInfo.InvariantCulture, out var us) && us >= 0)
                {
                    Interlocked.Exchange(ref job.OutTimeUs, us);
                }
                break;
            case "total_size":
                if (long.TryParse(value, NumberStyles.Integer, CultureInfo.InvariantCulture, out var size) && size >= 0)
                {
                    Interlocked.Exchange(ref job.TotalBytes, size);
                }
                break;
            case "speed":
                var trimmed = value.TrimEnd('x');
                if (double.TryParse(trimmed, NumberStyles.Float, CultureInfo.InvariantCulture, out var speed))
                {
                    job.Speed = speed;
                }
                break;
        }
    }

    // ---- Terminal-state helpers ------------------------------------------

    private void FailJob(TrailerJob job, string phase, string reason, int? exitCode, string stderrTail, string command)
    {
        job.Failed = true;
        job.Phase = JobPhases.Failed;
        job.Error = reason;
        job.EndedUtc = DateTime.UtcNow;
        var elapsedMs = (long)(job.EndedUtc.Value - job.StartedUtc).TotalMilliseconds;

        _recentFailures[job.VideoId] = new FailureMark(DateTime.UtcNow, reason);
        _diagnostics.RecordFailure(new TrailerFailure(
            job.VideoId, job.Label, phase, job.EndedUtc.Value, exitCode, reason, stderrTail, command, elapsedMs));
        var measured = MeasureBundle(job.VideoId);
        PushHistory(new JobHistoryEntry(
            job.VideoId, job.Label, job.Title, job.Source, "failed",
            job.StartedUtc, job.EndedUtc.Value, elapsedMs,
            measured.Bytes, measured.Segments, reason, stderrTail, command));

        // Jobs that die before ffmpeg starts never reach the exit monitor, so
        // evict here too; the monitor's own removal is a no-op in that case.
        _jobs.TryRemove(new KeyValuePair<string, TrailerJob>(job.VideoId, job));
    }

    private void CancelJob(TrailerJob job, string reason)
    {
        _recentCancels[job.VideoId] = DateTime.UtcNow;
        job.Canceled = true;
        job.Phase = JobPhases.Canceled;
        job.Error = reason;
        job.EndedUtc ??= DateTime.UtcNow;
        var elapsedMs = (long)(job.EndedUtc.Value - job.StartedUtc).TotalMilliseconds;
        var measured = MeasureBundle(job.VideoId);
        PushHistory(new JobHistoryEntry(
            job.VideoId, job.Label, job.Title, job.Source, "canceled",
            job.StartedUtc, job.EndedUtc.Value, elapsedMs,
            measured.Bytes, measured.Segments, reason, null, null));
        // A cancel is an explicit admin decision, not evidence the video is bad
        // — deliberately no negative-cache entry, so the next request retries.
        _jobs.TryRemove(new KeyValuePair<string, TrailerJob>(job.VideoId, job));
    }

    // ---- Bundle sidecar ---------------------------------------------------

    private void WriteSidecar(string dir, BundleSidecar sidecar)
    {
        try
        {
            File.WriteAllText(Path.Combine(dir, SidecarName), JsonSerializer.Serialize(sidecar));
        }
        catch (Exception ex)
        {
            _logger.LogDebug(ex, "[YouTubeTrailers] could not write bundle sidecar in {Dir}", dir);
        }
    }

    private static BundleSidecar? ReadSidecar(string dir)
    {
        try
        {
            var path = Path.Combine(dir, SidecarName);
            return File.Exists(path) ? JsonSerializer.Deserialize<BundleSidecar>(File.ReadAllText(path)) : null;
        }
        catch
        {
            return null;
        }
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

    private readonly record struct FailureMark(DateTime WhenUtc, string Reason);

    /// <summary>Phase constants — also the strings the dashboard renders as badges.</summary>
    public static class JobPhases
    {
        public const string Resolving = "resolving";
        public const string Queued = "queued";
        public const string Building = "building";
        public const string Completed = "completed";
        public const string Failed = "failed";
        public const string Canceled = "canceled";
    }

    /// <summary>Mutable live state for one in-flight build.</summary>
    public sealed class TrailerJob
    {
        public TrailerJob(string videoId, string source, string? label)
        {
            VideoId = videoId;
            Source = source;
            Label = label;
            StartedUtc = DateTime.UtcNow;
            RunTask = Task.CompletedTask;
            Cts = new CancellationTokenSource();
        }

        public string VideoId { get; }
        public string Source { get; }
        public DateTime StartedUtc { get; }
        public CancellationTokenSource Cts { get; }

        public string? Label { get; set; }
        public string? Title { get; set; }
        public double? DurationSeconds { get; set; }
        public string? Command { get; set; }
        public string? Error { get; set; }
        public DateTime? BuildStartedUtc { get; set; }
        public DateTime? EndedUtc { get; set; }
        public Process? Process { get; set; }
        public Task RunTask { get; set; }

        public volatile string Phase = JobPhases.Resolving;
        public volatile bool Failed;
        public volatile bool Canceled;
        public volatile bool WatchdogKilled;

        // Written by the progress reader thread, read by the dashboard poll —
        // Interlocked because long isn't atomic on 32-bit runtimes.
        public long OutTimeUs;
        public long TotalBytes;
        public double Speed;

        public bool IsFinished =>
            Phase is JobPhases.Completed or JobPhases.Failed or JobPhases.Canceled;
    }
}

/// <summary>Serializable view of a live build for the dashboard.</summary>
public sealed record JobSnapshot(
    string VideoId,
    string? Label,
    string? Title,
    string Source,
    string Phase,
    DateTime StartedUtc,
    double? DurationSeconds,
    double? Percent,
    double? Speed,
    long Bytes,
    int Segments,
    bool Playable);

/// <summary>A finished build, kept in a bounded ring for the "recent activity" list.</summary>
public sealed record JobHistoryEntry(
    string VideoId,
    string? Label,
    string? Title,
    string Source,
    string Outcome,
    DateTime StartedUtc,
    DateTime EndedUtc,
    long ElapsedMs,
    long Bytes,
    int Segments,
    string? Error,
    string? StderrTail,
    string? Command);

/// <summary>One cached bundle on disk.</summary>
public sealed record BundleInfo(
    string VideoId,
    string? Title,
    long Bytes,
    int Segments,
    bool Complete,
    DateTime LastWriteUtc,
    DateTime LastUsedUtc,
    double? DurationSeconds,
    bool Building);

/// <summary>A video that is fast-failing until its negative-cache entry expires.</summary>
public sealed record NegativeCacheEntry(string VideoId, DateTime FailedUtc, DateTime RetryAfterUtc, string Reason);

/// <summary>On-disk metadata written next to a bundle so titles survive a restart.</summary>
public sealed record BundleSidecar(
    string VideoId, string? Title, double? DurationSeconds, DateTime BuiltUtc, string? Label, string? Source);
