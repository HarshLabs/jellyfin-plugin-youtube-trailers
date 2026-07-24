using System;
using System.Threading;
using System.Threading.Tasks;
using MediaBrowser.Controller.Entities;
using MediaBrowser.Controller.Library;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;

namespace Jellyfin.Plugin.YouTubeTrailers.Services;

/// <summary>
/// Subscribes to <see cref="ILibraryManager.ItemAdded"/> and queues a trailer
/// build for newly added media, so a movie added tonight already has a warm,
/// instantly-playable trailer the first time anyone opens it.
///
/// The item is queued by ID with a delay rather than resolved immediately:
/// ItemAdded fires when the file is discovered, which is *before* the metadata
/// provider attaches RemoteTrailers. Looking for the YouTube link at that
/// instant finds nothing on a fresh import. <see cref="PrewarmQueue"/> re-reads
/// the item when the entry comes due, by which time TMDb has usually answered.
///
/// Default-off: a first library scan can fire thousands of ItemAdded events,
/// and each trailer is a real yt-dlp + ffmpeg job against YouTube. Opt in once
/// you're happy with the concurrency setting.
/// </summary>
public sealed class LibraryAddListener : IHostedService
{
    private readonly ILibraryManager _libraryManager;
    private readonly PrewarmQueue _queue;
    private readonly ILogger<LibraryAddListener> _logger;

    public LibraryAddListener(ILibraryManager libraryManager, PrewarmQueue queue, ILogger<LibraryAddListener> logger)
    {
        _libraryManager = libraryManager;
        _queue = queue;
        _logger = logger;
    }

    public Task StartAsync(CancellationToken cancellationToken)
    {
        _libraryManager.ItemAdded += OnItemAdded;
        _libraryManager.ItemUpdated += OnItemUpdated;
        _logger.LogInformation("[YouTubeTrailers] library-add listener hooked");
        return Task.CompletedTask;
    }

    public Task StopAsync(CancellationToken cancellationToken)
    {
        _libraryManager.ItemAdded -= OnItemAdded;
        _libraryManager.ItemUpdated -= OnItemUpdated;
        return Task.CompletedTask;
    }

    private void OnItemAdded(object? sender, ItemChangeEventArgs e) => Consider(e, "library-add");

    /// <summary>
    /// Metadata refreshes are the moment a trailer link usually *appears* — an
    /// item added before its provider lookup completed gets its RemoteTrailers
    /// on the subsequent update. The queue de-duplicates by item, and already
    /// cached videos are skipped, so hooking this is cheap.
    /// </summary>
    private void OnItemUpdated(object? sender, ItemChangeEventArgs e) => Consider(e, "metadata-update");

    private void Consider(ItemChangeEventArgs e, string source)
    {
        var cfg = Plugin.Instance?.Configuration;
        if (cfg is null || !cfg.Enabled || !cfg.PrewarmOnLibraryAdd)
        {
            return;
        }
        if (e.Item is not BaseItem item || item.IsFolder || item.IsVirtualItem)
        {
            return;
        }
        if (item is not (MediaBrowser.Controller.Entities.Movies.Movie
            or MediaBrowser.Controller.Entities.TV.Series
            or MediaBrowser.Controller.Entities.Video))
        {
            return;
        }

        var delay = TimeSpan.FromSeconds(Math.Clamp(cfg.LibraryAddDelaySeconds, 0, 3600));
        if (_queue.EnqueueItem(item.Id, source, delay))
        {
            _logger.LogDebug(
                "[YouTubeTrailers] {Source}: queued trailer prewarm for \"{Name}\" in {Delay}s",
                source, item.Name, delay.TotalSeconds);
        }
    }
}
