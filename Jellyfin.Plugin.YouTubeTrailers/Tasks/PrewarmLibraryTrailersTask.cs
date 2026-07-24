using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using Jellyfin.Plugin.YouTubeTrailers.Services;
using MediaBrowser.Model.Tasks;
using Microsoft.Extensions.Logging;

namespace Jellyfin.Plugin.YouTubeTrailers.Tasks;

/// <summary>
/// Walks the library off-hours and builds a trailer bundle for every item whose
/// metadata carries a YouTube trailer link but whose bundle isn't cached yet.
///
/// This is the backstop for the live library-add listener: items imported while
/// the listener was off, items whose metadata arrived late, and bundles evicted
/// by the prune task all get picked up on the next run. Once it has run, opening
/// any movie plays its trailer instantly.
///
/// Off by default — it's a lot of outbound requests to YouTube — and gated on
/// the config toggle rather than on the trigger, so the task stays visible (and
/// manually runnable) in the dashboard's scheduled-task list either way.
/// </summary>
public sealed class PrewarmLibraryTrailersTask : IScheduledTask
{
    private readonly TrailerResolver _resolver;
    private readonly TrailerIndex _index;
    private readonly ILogger<PrewarmLibraryTrailersTask> _logger;

    public PrewarmLibraryTrailersTask(
        TrailerResolver resolver, TrailerIndex index, ILogger<PrewarmLibraryTrailersTask> logger)
    {
        _resolver = resolver;
        _index = index;
        _logger = logger;
    }

    public string Name => "Cache YouTube trailers for library items";
    public string Key => "YouTubeTrailersPrewarmLibrary";
    public string Description =>
        "Builds and caches a trailer bundle for every library item that has a YouTube trailer link and isn't cached yet.";
    public string Category => "YouTube Trailers";

    public IEnumerable<TaskTriggerInfo> GetDefaultTriggers() =>
    [
        // 3 AM — an hour before the prune task, so a sweep isn't immediately
        // trimmed by the size cap it just filled.
        new TaskTriggerInfo
        {
            Type = TaskTriggerInfoType.DailyTrigger,
            TimeOfDayTicks = TimeSpan.FromHours(3).Ticks
        }
    ];

    public async Task ExecuteAsync(IProgress<double> progress, CancellationToken cancellationToken)
    {
        var cfg = Plugin.Instance?.Configuration;
        if (cfg is null || !cfg.Enabled)
        {
            _logger.LogInformation("[YouTubeTrailers] Library trailer sweep skipped — plugin disabled.");
            progress.Report(100);
            return;
        }
        if (!cfg.EnableLibraryPrewarmTask)
        {
            _logger.LogInformation(
                "[YouTubeTrailers] Library trailer sweep skipped — enable \"Cache trailers for the whole library\" in the plugin settings to turn it on.");
            progress.Report(100);
            return;
        }

        var snapshot = _index.Get(forceRebuild: true);

        // Honour the "only these libraries" setting, matching on the normalised
        // GUID form so a value stored with or without dashes still matches.
        var allowed = (cfg.PrewarmLibraryIds ?? [])
            .Select(id => Guid.TryParse(id, out var g) ? g.ToString("N") : null)
            .Where(id => id is not null)
            .ToHashSet(StringComparer.Ordinal);

        var candidates = snapshot.Trailers
            .Where(t => allowed.Count == 0 || allowed.Contains(t.LibraryId.ToString("N")))
            .Where(t => cfg.PrewarmAllTrailersPerItem || t.IsPrimary)
            .GroupBy(t => t.VideoId, StringComparer.Ordinal)
            .Select(g => g.First())
            .Where(t => !_resolver.IsComplete(t.VideoId))
            .ToList();

        if (cfg.MaxPrewarmPerRun > 0 && candidates.Count > cfg.MaxPrewarmPerRun)
        {
            _logger.LogInformation(
                "[YouTubeTrailers] Library trailer sweep: {Total} uncached, capped to {Cap} this run",
                candidates.Count, cfg.MaxPrewarmPerRun);
            candidates = candidates.Take(cfg.MaxPrewarmPerRun).ToList();
        }

        if (candidates.Count == 0)
        {
            _logger.LogInformation(
                "[YouTubeTrailers] Library trailer sweep: nothing to do ({Links} YouTube link(s) already cached)",
                snapshot.Trailers.Count);
            progress.Report(100);
            return;
        }

        _logger.LogInformation(
            "[YouTubeTrailers] Library trailer sweep: building {Count} trailer(s)", candidates.Count);

        var built = 0;
        var failed = 0;
        for (var i = 0; i < candidates.Count; i++)
        {
            cancellationToken.ThrowIfCancellationRequested();
            var t = candidates[i];

            // StartIfNeededAsync blocks on the build-slot semaphore, so this
            // serial loop keeps exactly MaxConcurrentBuilds jobs in flight
            // without needing its own throttle.
            var ok = await _resolver
                .StartIfNeededAsync(t.VideoId, cancellationToken, "scheduled-task", t.DisplayName)
                .ConfigureAwait(false);
            if (ok) built++; else failed++;

            progress.Report((i + 1) * 100d / candidates.Count);
        }

        _logger.LogInformation(
            "[YouTubeTrailers] Library trailer sweep finished: {Built} started, {Failed} failed to start",
            built, failed);
        progress.Report(100);
    }
}
