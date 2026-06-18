using System;
using System.Collections.Generic;
using System.Threading;
using System.Threading.Tasks;
using Jellyfin.Plugin.YouTubeTrailers.Services;
using MediaBrowser.Model.Tasks;
using Microsoft.Extensions.Logging;

namespace Jellyfin.Plugin.YouTubeTrailers.Tasks;

/// <summary>
/// Daily maintenance task that bounds the trailer cache: evicts bundles older
/// than <c>MaxAgeDays</c> and trims the least-recently-used bundles until the
/// cache is under <c>MaxCacheGigabytes</c>. Without this the cache grows
/// unbounded (every resolved trailer is ~10–30 MB on disk).
/// </summary>
public sealed class PruneCacheTask : IScheduledTask
{
    private readonly TrailerResolver _resolver;
    private readonly ILogger<PruneCacheTask> _logger;

    public PruneCacheTask(TrailerResolver resolver, ILogger<PruneCacheTask> logger)
    {
        _resolver = resolver;
        _logger = logger;
    }

    public string Name => "Prune YouTube trailer cache";
    public string Key => "YouTubeTrailersPruneCache";
    public string Description => "Evicts old / least-recently-used cached YouTube trailer bundles to bound disk usage.";
    public string Category => "YouTube Trailers";

    public IEnumerable<TaskTriggerInfo> GetDefaultTriggers() =>
    [
        // Daily at ~4 AM, off-hours so eviction can't race active playback.
        new TaskTriggerInfo
        {
            Type = TaskTriggerInfoType.DailyTrigger,
            TimeOfDayTicks = TimeSpan.FromHours(4).Ticks
        }
    ];

    public Task ExecuteAsync(IProgress<double> progress, CancellationToken cancellationToken)
    {
        var cfg = Plugin.Instance?.Configuration;
        if (cfg is null)
        {
            return Task.CompletedTask;
        }

        var maxBytes = cfg.MaxCacheGigabytes > 0 ? (long)cfg.MaxCacheGigabytes * 1024 * 1024 * 1024 : 0;
        var before = _resolver.CacheStats();
        var removed = _resolver.PruneCache(cfg.MaxAgeDays, maxBytes);
        var after = _resolver.CacheStats();

        _logger.LogInformation(
            "[YouTubeTrailers] Prune: removed {Removed} bundle(s); {BeforeMB} MB → {AfterMB} MB ({Count} remain)",
            removed, before.Bytes / (1024 * 1024), after.Bytes / (1024 * 1024), after.Count);

        progress.Report(100);
        return Task.CompletedTask;
    }
}
