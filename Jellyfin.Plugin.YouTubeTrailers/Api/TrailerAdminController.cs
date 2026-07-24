using System;
using System.Collections.Generic;
using System.Globalization;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using Jellyfin.Plugin.YouTubeTrailers.Services;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using Microsoft.Extensions.Logging;

namespace Jellyfin.Plugin.YouTubeTrailers.Api;

/// <summary>
/// Admin-only endpoints powering the dashboard config page: live job monitoring
/// and cancellation, the cached-bundle browser, library trailer coverage and
/// bulk prewarming, the failure log, and the per-video diagnose runner.
///
/// Everything here requires elevation — these endpoints start real yt-dlp and
/// ffmpeg work, delete cached data, and surface tool output that can include a
/// configured cookies path.
/// </summary>
[ApiController]
[Authorize(Policy = "RequiresElevation")]
[Route("Trailers/admin")]
public sealed class TrailerAdminController : ControllerBase
{
    private readonly TrailerResolver _resolver;
    private readonly TrailerIndex _index;
    private readonly TrailerDiagnostics _diagnostics;
    private readonly PrewarmQueue _queue;
    private readonly YtDlpManager _ytDlp;
    private readonly ToolRunner _tools;
    private readonly ILogger<TrailerAdminController> _logger;

    public TrailerAdminController(
        TrailerResolver resolver,
        TrailerIndex index,
        TrailerDiagnostics diagnostics,
        PrewarmQueue queue,
        YtDlpManager ytDlp,
        ToolRunner tools,
        ILogger<TrailerAdminController> logger)
    {
        _resolver = resolver;
        _index = index;
        _diagnostics = diagnostics;
        _queue = queue;
        _ytDlp = ytDlp;
        _tools = tools;
        _logger = logger;
    }

    // ── Overview ───────────────────────────────────────────────────────────

    /// <summary>
    /// Dashboard headline numbers: cache size, live work, recent failures, and
    /// per-library trailer coverage. Deliberately excludes the yt-dlp version
    /// (that's a process spawn — see <see cref="ToolVersion"/>) because the page
    /// polls this endpoint.
    /// </summary>
    [HttpGet("stats")]
    public ActionResult<StatsResponse> Stats([FromQuery] bool refreshIndex = false)
    {
        var (bytes, count) = _resolver.CacheStats();
        var bundles = _resolver.ListBundles();
        var snapshot = _index.Get(refreshIndex);

        // Coverage counts the PRIMARY trailer per item: that's the one a client
        // plays, so "covered" should mean "the trailer someone will actually
        // request is warm", not "some clip for this movie happens to be cached".
        var cachedVideoIds = bundles.Where(b => b.Complete)
            .Select(b => b.VideoId).ToHashSet(StringComparer.Ordinal);

        var libraries = new List<LibraryCoverage>();
        foreach (var lib in snapshot.Libraries)
        {
            var primaries = snapshot.Trailers
                .Where(t => t.LibraryId == lib.Id && t.IsPrimary)
                .ToList();
            libraries.Add(new LibraryCoverage(
                lib.Id, lib.Name, lib.TotalItems, lib.ItemsWithTrailers,
                primaries.Count(t => cachedVideoIds.Contains(t.VideoId))));
        }

        var allPrimaries = snapshot.Trailers.Where(t => t.IsPrimary).ToList();

        // Success rate over the retained history ring. Cancellations are
        // excluded: an admin killing a build says nothing about whether trailer
        // extraction is healthy, which is the question this number answers.
        var history = _resolver.History(100);
        var decided = history.Count(h => h.Outcome is "completed" or "failed");
        int? successRate = decided == 0
            ? null
            : (int)Math.Round(history.Count(h => h.Outcome == "completed") * 100d / decided);

        return new StatsResponse(
            Plugin.Instance?.Configuration.Enabled ?? false,
            bytes,
            count,
            bundles.Count(b => b.Complete),
            _resolver.ActiveJobs().Count,
            _queue.PendingCount,
            _diagnostics.FailureCountSince(DateTime.UtcNow.AddHours(-24)),
            _resolver.NegativeCache().Count,
            _resolver.MaxConcurrentBuilds,
            Environment.ProcessorCount,
            successRate,
            snapshot.Trailers.Count,
            allPrimaries.Count,
            allPrimaries.Count(t => cachedVideoIds.Contains(t.VideoId)),
            libraries,
            snapshot.BuiltUtc,
            snapshot.Error,
            DateTime.UtcNow);
    }

    /// <summary>Installed yt-dlp version + which binary is in use (managed vs configured).</summary>
    [HttpGet("version")]
    public async Task<ActionResult<VersionResponse>> ToolVersion(CancellationToken cancellationToken)
    {
        var version = await _resolver.YtDlpVersionAsync(cancellationToken).ConfigureAwait(false);
        return new VersionResponse(version, !_ytDlp.UsingConfigured, _tools.ResolveFfmpeg() ?? "(none)");
    }

    /// <summary>Downloads / updates the plugin-managed yt-dlp binary.</summary>
    [HttpPost("ytdlp/update")]
    public async Task<ActionResult<UpdateResponse>> UpdateYtDlp(CancellationToken cancellationToken)
    {
        var (ok, message) = await _ytDlp.DownloadAsync(cancellationToken).ConfigureAwait(false);
        var version = ok
            ? await _resolver.YtDlpVersionAsync(cancellationToken).ConfigureAwait(false)
            : "error";
        return new UpdateResponse(ok, message, version);
    }

    // ── Live jobs ──────────────────────────────────────────────────────────

    /// <summary>
    /// Everything currently in flight plus what just finished and what is
    /// temporarily blocked. Polled every couple of seconds while work is active.
    /// </summary>
    [HttpGet("jobs")]
    public ActionResult<JobsResponse> Jobs([FromQuery] int history = 15)
    {
        var snapshot = _index.Get();
        return new JobsResponse(
            DateTime.UtcNow,
            _resolver.MaxConcurrentBuilds,
            _queue.PendingCount,
            _queue.PendingVideoIds,
            _resolver.ActiveJobs().Select(j => Label(j, snapshot)).ToList(),
            _resolver.History(Math.Clamp(history, 1, 100)).Select(h => Label(h, snapshot)).ToList(),
            _resolver.NegativeCache());
    }

    /// <summary>
    /// Fills in a human label for jobs started by a raw playback request, which
    /// only ever knows a video ID. The library index knows which movie that ID
    /// belongs to, so the dashboard can show "Dune: Part Two (2024)" instead of
    /// an opaque 11-character string.
    /// </summary>
    private static JobSnapshot Label(JobSnapshot job, TrailerIndex.IndexSnapshot idx) =>
        job.Label is not null || !idx.ByVideoId.TryGetValue(job.VideoId, out var t)
            ? job
            : job with { Label = t.DisplayName };

    private static JobHistoryEntry Label(JobHistoryEntry entry, TrailerIndex.IndexSnapshot idx) =>
        entry.Label is not null || !idx.ByVideoId.TryGetValue(entry.VideoId, out var t)
            ? entry
            : entry with { Label = t.DisplayName };

    /// <summary>Cancels specific in-flight builds. Already-cached bundles are untouched.</summary>
    [HttpPost("jobs/cancel")]
    public ActionResult<MutationResponse> CancelJobs([FromBody] VideoIdsRequest request)
    {
        if (request?.VideoIds is null || request.VideoIds.Count == 0)
        {
            return new MutationResponse(0, 0);
        }
        int canceled = 0, skipped = 0;
        foreach (var id in request.VideoIds)
        {
            if (_resolver.TryCancel(id)) canceled++; else skipped++;
        }
        _logger.LogInformation("[YouTubeTrailers] admin cancel: {Canceled} canceled, {Skipped} skipped", canceled, skipped);
        return new MutationResponse(canceled, skipped);
    }

    /// <summary>Cancels every in-flight build and empties the prewarm queue.</summary>
    [HttpPost("jobs/cancel-all")]
    public ActionResult<MutationResponse> CancelAllJobs()
    {
        var canceled = _resolver.CancelAll();
        var dropped = _queue.Clear();
        _logger.LogInformation(
            "[YouTubeTrailers] admin cancel-all: {Canceled} build(s) killed, {Dropped} queued prewarm(s) dropped",
            canceled, dropped);
        return new MutationResponse(canceled, dropped);
    }

    // ── Cached bundles ─────────────────────────────────────────────────────

    /// <summary>Browsable list of what's on disk, with the owning library item where known.</summary>
    [HttpGet("bundles")]
    public ActionResult<BundlesResponse> Bundles(
        [FromQuery] string? search,
        [FromQuery] string status = "all",
        [FromQuery] string sort = "recent",
        [FromQuery] int offset = 0,
        [FromQuery] int limit = 100)
    {
        offset = Math.Max(0, offset);
        limit = Math.Clamp(limit, 1, 1000);

        var idx = _index.Get();
        IEnumerable<BundleRow> rows = _resolver.ListBundles().Select(b => new BundleRow(
            b.VideoId,
            b.Title,
            idx.ByVideoId.TryGetValue(b.VideoId, out var t) ? t.DisplayName : null,
            idx.ByVideoId.TryGetValue(b.VideoId, out var t2) ? t2.LibraryName : null,
            b.Bytes,
            b.Segments,
            b.Complete,
            b.Building,
            b.LastWriteUtc,
            b.LastUsedUtc,
            b.DurationSeconds));

        if (!string.IsNullOrWhiteSpace(search))
        {
            rows = rows.Where(r =>
                ContainsLoose(r.Title, search) ||
                ContainsLoose(r.ItemName, search) ||
                ContainsLoose(r.VideoId, search));
        }
        rows = status.ToLowerInvariant() switch
        {
            "complete" => rows.Where(r => r.Complete),
            "partial" => rows.Where(r => !r.Complete),
            _ => rows,
        };

        var ci = StringComparer.OrdinalIgnoreCase;
        rows = sort.ToLowerInvariant() switch
        {
            "name" => rows.OrderBy(r => r.ItemName ?? r.Title ?? r.VideoId, ci),
            "size" => rows.OrderByDescending(r => r.Bytes),
            "size_asc" => rows.OrderBy(r => r.Bytes),
            "oldest" => rows.OrderBy(r => r.LastWriteUtc),
            "unused" => rows.OrderBy(r => r.LastUsedUtc),
            _ => rows.OrderByDescending(r => r.LastWriteUtc),
        };

        var all = rows.ToList();
        return new BundlesResponse(all.Count, offset, limit, all.Skip(offset).Take(limit).ToList());
    }

    /// <summary>Deletes specific cached bundles. Bundles with a live build are skipped.</summary>
    [HttpPost("bundles/delete")]
    public ActionResult<MutationResponse> DeleteBundles([FromBody] VideoIdsRequest request)
    {
        if (request?.VideoIds is null || request.VideoIds.Count == 0)
        {
            return new MutationResponse(0, 0);
        }
        int deleted = 0, skipped = 0;
        foreach (var id in request.VideoIds)
        {
            if (_resolver.DeleteBundle(id)) deleted++; else skipped++;
        }
        _logger.LogInformation("[YouTubeTrailers] admin delete: {Deleted} bundle(s) removed, {Skipped} skipped", deleted, skipped);
        return new MutationResponse(deleted, skipped);
    }

    /// <summary>Clears the whole trailer cache.</summary>
    [HttpPost("clear")]
    public ActionResult<MutationResponse> ClearCache()
    {
        var removed = _resolver.ClearCache();
        _logger.LogInformation("[YouTubeTrailers] Cache cleared via admin: {Removed} bundle(s) removed", removed);
        return new MutationResponse(removed, 0);
    }

    // ── Library coverage + bulk prewarm ────────────────────────────────────

    /// <summary>
    /// Library items that carry a YouTube trailer link, with their cache state.
    /// This is the work-list view: filter to "not cached" and prewarm the lot.
    /// </summary>
    [HttpGet("library")]
    public ActionResult<LibraryResponse> Library(
        [FromQuery] string? search,
        [FromQuery] Guid? libraryId,
        [FromQuery] string cached = "all",
        [FromQuery] string sort = "name",
        [FromQuery] int offset = 0,
        [FromQuery] int limit = 100,
        [FromQuery] bool refresh = false)
    {
        offset = Math.Max(0, offset);
        limit = Math.Clamp(limit, 1, 1000);

        var rows = FilterLibrary(search, libraryId, cached, refresh);
        var ci = StringComparer.OrdinalIgnoreCase;
        IEnumerable<LibraryRow> sorted = sort.ToLowerInvariant() switch
        {
            "name_desc" => rows.OrderByDescending(r => r.ItemName, ci),
            "cached" => rows.OrderByDescending(r => r.Cached).ThenBy(r => r.ItemName, ci),
            "uncached" => rows.OrderBy(r => r.Cached).ThenBy(r => r.ItemName, ci),
            "year" => rows.OrderByDescending(r => r.Year ?? 0).ThenBy(r => r.ItemName, ci),
            _ => rows.OrderBy(r => r.ItemName, ci),
        };

        var all = sorted.ToList();
        return new LibraryResponse(all.Count, offset, limit, all.Skip(offset).Take(limit).ToList());
    }

    private List<LibraryRow> FilterLibrary(string? search, Guid? libraryId, string? cached, bool refresh)
    {
        var idx = _index.Get(refresh);
        var cfg = Plugin.Instance?.Configuration;
        var bundles = _resolver.ListBundles().Where(b => b.Complete)
            .Select(b => b.VideoId).ToHashSet(StringComparer.Ordinal);

        IEnumerable<TrailerIndex.LibraryTrailer> source = idx.Trailers;
        if (cfg?.PrewarmAllTrailersPerItem != true)
        {
            source = source.Where(t => t.IsPrimary);
        }
        if (libraryId.HasValue && libraryId.Value != Guid.Empty)
        {
            source = source.Where(t => t.LibraryId == libraryId.Value);
        }
        if (!string.IsNullOrWhiteSpace(search))
        {
            source = source.Where(t => ContainsLoose(t.ItemName, search) || ContainsLoose(t.VideoId, search));
        }

        var rows = source.Select(t => new LibraryRow(
            t.ItemId, t.ItemName, t.Year, t.ItemType, t.LibraryName, t.VideoId, t.TrailerName,
            bundles.Contains(t.VideoId), _resolver.IsBuilding(t.VideoId))).ToList();

        if (!string.IsNullOrEmpty(cached) && !cached.Equals("all", StringComparison.OrdinalIgnoreCase))
        {
            var want = cached.Equals("true", StringComparison.OrdinalIgnoreCase);
            rows = rows.Where(r => r.Cached == want).ToList();
        }
        return rows;
    }

    /// <summary>
    /// Queues specific video IDs for a background build.
    /// Routed under <c>prewarm/videos</c> rather than a bare <c>prewarm</c> so it
    /// can never collide with the playback controller's <c>Trailers/{videoId}/prewarm</c>.
    /// </summary>
    [HttpPost("prewarm/videos")]
    public ActionResult<MutationResponse> Prewarm([FromBody] VideoIdsRequest request)
    {
        if (request?.VideoIds is null || request.VideoIds.Count == 0)
        {
            return new MutationResponse(0, 0);
        }
        var idx = _index.Get();
        int queued = 0, skipped = 0;
        foreach (var raw in request.VideoIds)
        {
            // Accept a pasted URL as readily as a bare ID — the admin box in the
            // UI takes either.
            var id = YouTubeLink.ExtractId(raw);
            if (id is null)
            {
                skipped++;
                continue;
            }
            var label = idx.ByVideoId.TryGetValue(id, out var t) ? t.DisplayName : null;
            if (_queue.EnqueueVideo(id, label, "admin")) queued++; else skipped++;
        }
        _logger.LogInformation("[YouTubeTrailers] admin prewarm: {Queued} queued, {Skipped} skipped", queued, skipped);
        return new MutationResponse(queued, skipped);
    }

    /// <summary>
    /// Queues every library item matching the current filter — the "prewarm all
    /// N uncached trailers" action, without the client having to page through
    /// every row first.
    /// </summary>
    [HttpPost("prewarm/matching")]
    public ActionResult<MutationResponse> PrewarmMatching([FromBody] LibraryFilterRequest request)
    {
        var rows = FilterLibrary(request?.Search, request?.LibraryId, request?.Cached, refresh: false);
        int queued = 0, skipped = 0;
        foreach (var row in rows)
        {
            if (_queue.EnqueueVideo(row.VideoId, row.DisplayName, "admin-bulk")) queued++; else skipped++;
        }
        _logger.LogInformation(
            "[YouTubeTrailers] admin prewarm-matching: {Queued} queued, {Skipped} skipped (search='{Search}' library={Library} cached={Cached})",
            queued, skipped, request?.Search ?? "", request?.LibraryId, request?.Cached ?? "all");
        return new MutationResponse(queued, skipped);
    }

    // ── Failures + diagnose ────────────────────────────────────────────────

    /// <summary>Recent build failures with the tool output that explains each one.</summary>
    [HttpGet("failures")]
    public ActionResult<FailuresResponse> Failures([FromQuery] int limit = 25)
    {
        var idx = _index.Get();
        var rows = _diagnostics.RecentFailures(Math.Clamp(limit, 1, 100))
            .Select(f => f.Label is not null || !idx.ByVideoId.TryGetValue(f.VideoId, out var t)
                ? f
                : f with { Label = t.DisplayName })
            .ToList();
        return new FailuresResponse(DateTime.UtcNow, rows, _resolver.NegativeCache());
    }

    /// <summary>
    /// Clears the failure log and the negative cache, so every previously failed
    /// video is retried on its next request. The usual follow-up to updating
    /// yt-dlp or fixing a proxy.
    /// </summary>
    [HttpPost("failures/clear")]
    public ActionResult<MutationResponse> ClearFailures()
    {
        var cleared = _diagnostics.ClearFailures();
        var unblocked = _resolver.ClearNegativeCache();
        _logger.LogInformation(
            "[YouTubeTrailers] admin cleared {Cleared} failure record(s) and unblocked {Unblocked} video(s)",
            cleared, unblocked);
        return new MutationResponse(cleared, unblocked);
    }

    /// <summary>
    /// Runs the full pipeline against one video and reports which stage breaks:
    /// binaries → YouTube metadata → format selection → CDN reachability → a real
    /// 3-second ffmpeg fetch. Accepts a bare ID or any YouTube URL.
    /// </summary>
    // Takes the video in the BODY, not the route: admins paste full YouTube URLs
    // here, and a percent-encoded "/" inside a route segment is rejected or
    // mangled by parts of the HTTP stack before routing ever sees it.
    [HttpPost("diagnose")]
    public async Task<ActionResult<DiagnoseReport>> Diagnose(
        [FromBody] DiagnoseRequest request, CancellationToken cancellationToken)
    {
        var id = YouTubeLink.ExtractId(request?.Video);
        if (id is null)
        {
            return BadRequest(new { message = "Not a YouTube video ID or URL." });
        }

        // Lead with what the plugin already believes about this video, so the
        // report explains a fast 404 (negative cache) as readily as a build error.
        var leading = new List<DiagStep>();
        var idx = _index.Get();
        if (idx.ByVideoId.TryGetValue(id, out var link))
        {
            leading.Add(new DiagStep("Library item", "ok",
                $"Linked to \"{link.DisplayName}\" in {link.LibraryName}"
                + (link.IsPrimary ? " (primary trailer)." : " (secondary trailer)."), null));
        }
        else
        {
            // Informational, not a warning: an ad-hoc test of a video that isn't
            // in the library is a perfectly normal thing to do, and flagging it
            // would drown out the verdict for the stages that actually matter.
            leading.Add(new DiagStep("Library item", "ok",
                "No library item references this video, so nothing will prewarm it automatically — "
                + "fine for an ad-hoc test.", null));
        }

        var complete = _resolver.IsComplete(id);
        var building = _resolver.IsBuilding(id);
        leading.Add(new DiagStep("Cache state",
            complete ? "ok" : "warn",
            complete
                ? "Fully cached — this trailer is served from disk with no yt-dlp or ffmpeg work."
                : building
                    ? "A build is running right now."
                    : "Not cached; a request would trigger a fresh resolve + remux.",
            null));

        var blocked = _resolver.NegativeCache().FirstOrDefault(n => n.VideoId == id);
        if (blocked is not null)
        {
            leading.Add(new DiagStep("Negative cache", "warn",
                $"Fast-failing until {blocked.RetryAfterUtc:HH:mm:ss} UTC after: {blocked.Reason} "
                + "Requests return 404 immediately during this window — use \"Retry blocked videos\" to clear it.",
                null));
        }

        var report = await _diagnostics.DiagnoseAsync(id, leading, cancellationToken).ConfigureAwait(false);
        _logger.LogInformation("[YouTubeTrailers] diagnose {VideoId}: {Verdict} — {Summary}",
            id, report.Verdict, report.Summary);
        return report;
    }

    /// <summary>Case- and accent-insensitive contains, so "pokemon" matches "Pokémon".</summary>
    private static bool ContainsLoose(string? haystack, string? needle)
    {
        if (string.IsNullOrEmpty(haystack) || string.IsNullOrEmpty(needle))
        {
            return false;
        }
        return CultureInfo.InvariantCulture.CompareInfo.IndexOf(
            haystack, needle, CompareOptions.IgnoreCase | CompareOptions.IgnoreNonSpace) >= 0;
    }

    // ── Wire types ─────────────────────────────────────────────────────────

    public sealed record StatsResponse(
        bool Enabled,
        long CacheBytes,
        int CachedBundles,
        int CompleteBundles,
        int ActiveJobs,
        int PendingPrewarm,
        int Failures24h,
        int BlockedVideos,
        int MaxConcurrentBuilds,
        int CpuCores,
        int? SuccessRate,
        int LibraryTrailerLinks,
        int LibraryItemsWithTrailers,
        int LibraryItemsCached,
        IReadOnlyList<LibraryCoverage> Libraries,
        DateTime IndexBuiltUtc,
        string? IndexError,
        DateTime ServerUtc);

    public sealed record LibraryCoverage(
        Guid Id, string Name, int TotalItems, int ItemsWithTrailers, int CachedTrailers);

    public sealed record VersionResponse(string YtDlp, bool Managed, string Ffmpeg);

    public sealed record UpdateResponse(bool Ok, string Message, string Version);

    public sealed record JobsResponse(
        DateTime ServerUtc,
        int MaxConcurrentBuilds,
        int PendingPrewarm,
        IReadOnlyList<string> PendingVideoIds,
        IReadOnlyList<JobSnapshot> Active,
        IReadOnlyList<JobHistoryEntry> Recent,
        IReadOnlyList<NegativeCacheEntry> Blocked);

    public sealed record BundlesResponse(int Total, int Offset, int Limit, IReadOnlyList<BundleRow> Items);

    public sealed record BundleRow(
        string VideoId,
        string? Title,
        string? ItemName,
        string? LibraryName,
        long Bytes,
        int Segments,
        bool Complete,
        bool Building,
        DateTime LastWriteUtc,
        DateTime LastUsedUtc,
        double? DurationSeconds);

    public sealed record LibraryResponse(int Total, int Offset, int Limit, IReadOnlyList<LibraryRow> Items);

    public sealed record LibraryRow(
        Guid ItemId,
        string ItemName,
        int? Year,
        string ItemType,
        string LibraryName,
        string VideoId,
        string? TrailerName,
        bool Cached,
        bool Building)
    {
        public string DisplayName => Year is > 0 ? $"{ItemName} ({Year})" : ItemName;
    }

    public sealed record FailuresResponse(
        DateTime ServerUtc, IReadOnlyList<TrailerFailure> Failures, IReadOnlyList<NegativeCacheEntry> Blocked);

    public sealed record VideoIdsRequest(List<string> VideoIds);

    public sealed record DiagnoseRequest(string? Video);

    public sealed record LibraryFilterRequest(string? Search, Guid? LibraryId, string? Cached);

    public sealed record MutationResponse(int Changed, int Skipped);
}
