using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using MediaBrowser.Controller.Entities;
using MediaBrowser.Controller.Library;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;

namespace Jellyfin.Plugin.YouTubeTrailers.Services;

/// <summary>
/// A single-consumer background queue for "build this trailer, but not right
/// now". Everything that wants a trailer cached without blocking a caller goes
/// through here: the library-add listener, the dashboard's bulk prewarm, and
/// the scheduled library sweep.
///
/// Two entry shapes:
///  * <b>by video ID</b> — the caller already knows what to build;
///  * <b>by item ID</b> — the caller only knows a library item, and the trailer
///    URLs are read at drain time. This matters for newly added media: Jellyfin
///    fires ItemAdded before the metadata provider has attached RemoteTrailers,
///    so resolving the link immediately would find nothing. A short delay plus
///    a late lookup is what makes auto-trailers actually work on import.
///
/// Draining is deliberately sequential. <see cref="TrailerResolver.StartIfNeededAsync"/>
/// returns once ffmpeg is spawned and blocks on the build-slot semaphore before
/// that, so a serial drain naturally saturates exactly MaxConcurrentBuilds
/// without needing its own parallelism knob.
/// </summary>
public sealed class PrewarmQueue : IHostedService
{
    // Hard cap so a first-time import of a 50k-item library can't turn into an
    // unbounded in-memory queue. Anything dropped is picked up by the daily
    // sweep task instead.
    private const int MaxPending = 20_000;

    private readonly TrailerResolver _resolver;
    private readonly ILibraryManager _libraryManager;
    private readonly ILogger<PrewarmQueue> _logger;

    private readonly object _sync = new();
    private readonly List<PendingEntry> _pending = [];
    private readonly HashSet<string> _queuedVideoIds = new(StringComparer.Ordinal);

    private CancellationTokenSource? _stopping;
    private Task? _worker;
    private long _processed;
    private long _dropped;

    public PrewarmQueue(TrailerResolver resolver, ILibraryManager libraryManager, ILogger<PrewarmQueue> logger)
    {
        _resolver = resolver;
        _libraryManager = libraryManager;
        _logger = logger;
    }

    public int PendingCount
    {
        get { lock (_sync) { return _pending.Count; } }
    }

    /// <summary>
    /// Video IDs waiting to build. The dashboard needs the actual IDs, not just
    /// a count, so it can grey out the Cache button on the exact rows that are
    /// already spoken for. Item-shaped entries are excluded: their trailer URL
    /// isn't resolved until drain time, so there is no video ID to report yet.
    /// </summary>
    public IReadOnlyList<string> PendingVideoIds
    {
        get
        {
            lock (_sync)
            {
                return _pending.Where(p => p.VideoId is not null).Select(p => p.VideoId!).ToList();
            }
        }
    }

    public long ProcessedCount => Interlocked.Read(ref _processed);

    public long DroppedCount => Interlocked.Read(ref _dropped);

    /// <summary>Queues a known video ID. Returns false when it's already queued or already cached.</summary>
    public bool EnqueueVideo(string videoId, string? label, string source, TimeSpan? delay = null)
    {
        if (!TrailerResolver.IsValidVideoId(videoId) || _resolver.IsComplete(videoId))
        {
            return false;
        }
        lock (_sync)
        {
            if (!_queuedVideoIds.Add(videoId))
            {
                return false;
            }
            if (_pending.Count >= MaxPending)
            {
                _queuedVideoIds.Remove(videoId);
                Interlocked.Increment(ref _dropped);
                return false;
            }
            _pending.Add(new PendingEntry(Guid.Empty, videoId, label, source, DateTime.UtcNow + (delay ?? TimeSpan.Zero)));
            return true;
        }
    }

    /// <summary>
    /// Queues a library item; its trailer URLs are read when the entry comes
    /// due, so a metadata fetch that lands after the item was added is still
    /// picked up.
    /// </summary>
    public bool EnqueueItem(Guid itemId, string source, TimeSpan delay)
    {
        if (itemId == Guid.Empty)
        {
            return false;
        }
        lock (_sync)
        {
            if (_pending.Any(p => p.ItemId == itemId))
            {
                return false;
            }
            if (_pending.Count >= MaxPending)
            {
                Interlocked.Increment(ref _dropped);
                return false;
            }
            _pending.Add(new PendingEntry(itemId, null, null, source, DateTime.UtcNow + delay));
            return true;
        }
    }

    public int Clear()
    {
        lock (_sync)
        {
            var n = _pending.Count;
            _pending.Clear();
            _queuedVideoIds.Clear();
            return n;
        }
    }

    public Task StartAsync(CancellationToken cancellationToken)
    {
        _stopping = new CancellationTokenSource();
        _worker = Task.Run(() => RunAsync(_stopping.Token), CancellationToken.None);
        return Task.CompletedTask;
    }

    public async Task StopAsync(CancellationToken cancellationToken)
    {
        try { _stopping?.Cancel(); } catch { /* ignore */ }
        if (_worker is not null)
        {
            // Bounded wait — a build in flight shouldn't delay server shutdown.
            await Task.WhenAny(_worker, Task.Delay(2000, CancellationToken.None)).ConfigureAwait(false);
        }
    }

    private async Task RunAsync(CancellationToken ct)
    {
        while (!ct.IsCancellationRequested)
        {
            try
            {
                var entry = TakeNextDue();
                if (entry is null)
                {
                    await Task.Delay(TimeSpan.FromSeconds(2), ct).ConfigureAwait(false);
                    continue;
                }
                await ProcessAsync(entry, ct).ConfigureAwait(false);
            }
            catch (OperationCanceledException)
            {
                return;
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "[YouTubeTrailers] prewarm queue worker error");
                try { await Task.Delay(TimeSpan.FromSeconds(5), ct).ConfigureAwait(false); }
                catch (OperationCanceledException) { return; }
            }
        }
    }

    /// <summary>
    /// Pops one due entry. Deliberately one at a time rather than draining the
    /// whole due set into a local list: the resolver blocks until a build slot
    /// frees, so a batched drain would leave the backlog sitting invisibly in
    /// this method's stack frame while the dashboard reported "0 queued".
    /// </summary>
    private PendingEntry? TakeNextDue()
    {
        var now = DateTime.UtcNow;
        lock (_sync)
        {
            var index = _pending.FindIndex(p => p.DueUtc <= now);
            if (index < 0)
            {
                return null;
            }
            var entry = _pending[index];
            _pending.RemoveAt(index);
            return entry;
        }
    }

    private async Task ProcessAsync(PendingEntry entry, CancellationToken ct)
    {
        var cfg = Plugin.Instance?.Configuration;
        if (cfg is null || !cfg.Enabled)
        {
            return;
        }

        if (entry.VideoId is not null)
        {
            await BuildAsync(entry.VideoId, entry.Label, entry.Source, ct).ConfigureAwait(false);
            return;
        }

        // Item-shaped entry: resolve the trailer links now that metadata has
        // had time to land.
        BaseItem? item;
        try
        {
            item = _libraryManager.GetItemById(entry.ItemId);
        }
        catch (Exception ex)
        {
            _logger.LogDebug(ex, "[YouTubeTrailers] prewarm lookup failed for item {ItemId}", entry.ItemId);
            return;
        }
        if (item is null)
        {
            return; // removed between enqueue and drain
        }

        if (!IsLibraryAllowed(item))
        {
            _logger.LogDebug(
                "[YouTubeTrailers] prewarm: skipping \"{Name}\" — its library isn't selected for automatic caching",
                item.Name);
            return;
        }

        var videoIds = TrailerIndex.VideoIdsFor(item);
        if (videoIds.Count == 0)
        {
            _logger.LogDebug(
                "[YouTubeTrailers] prewarm: {Name} has no YouTube trailer link yet — the scheduled sweep will retry once metadata lands",
                item.Name);
            return;
        }

        var label = TrailerIndex.DisplayNameFor(item);
        var take = cfg.PrewarmAllTrailersPerItem ? videoIds.Count : 1;
        foreach (var videoId in videoIds.Take(take))
        {
            await BuildAsync(videoId, label, entry.Source, ct).ConfigureAwait(false);
        }
    }

    /// <summary>
    /// Honours the "only these libraries" setting. Resolved per item at drain
    /// time via GetCollectionFolders rather than at enqueue time, so the check
    /// costs nothing on servers that haven't restricted anything (the common
    /// case short-circuits on the empty list).
    /// </summary>
    private bool IsLibraryAllowed(BaseItem item)
    {
        var allowed = Plugin.Instance?.Configuration.PrewarmLibraryIds;
        if (allowed is null || allowed.Length == 0)
        {
            return true;
        }
        try
        {
            foreach (var folder in _libraryManager.GetCollectionFolders(item))
            {
                var id = folder.Id.ToString("N");
                foreach (var candidate in allowed)
                {
                    if (Guid.TryParse(candidate, out var parsed) && parsed.ToString("N") == id)
                    {
                        return true;
                    }
                }
            }
        }
        catch (Exception ex)
        {
            // Can't determine the library — allow rather than silently skipping,
            // so a lookup failure never quietly disables automatic caching.
            _logger.LogDebug(ex, "[YouTubeTrailers] could not resolve libraries for {ItemId}", item.Id);
            return true;
        }
        return false;
    }

    private async Task BuildAsync(string videoId, string? label, string source, CancellationToken ct)
    {
        lock (_sync)
        {
            _queuedVideoIds.Remove(videoId);
        }
        if (_resolver.IsComplete(videoId))
        {
            return;
        }
        try
        {
            await _resolver.StartIfNeededAsync(videoId, ct, source, label).ConfigureAwait(false);
            Interlocked.Increment(ref _processed);
        }
        catch (OperationCanceledException)
        {
            throw;
        }
        catch (Exception ex)
        {
            // Individual failures are already recorded in the diagnostics log by
            // the resolver; never let one bad video kill the drain loop.
            _logger.LogDebug(ex, "[YouTubeTrailers] prewarm build threw for {VideoId}", videoId);
        }
    }

    private sealed record PendingEntry(Guid ItemId, string? VideoId, string? Label, string Source, DateTime DueUtc);
}
