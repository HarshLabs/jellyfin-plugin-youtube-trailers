using System;
using System.Collections.Generic;
using System.Linq;
using Jellyfin.Data.Enums;
using MediaBrowser.Controller.Entities;
using MediaBrowser.Controller.Library;
using Microsoft.Extensions.Logging;

namespace Jellyfin.Plugin.YouTubeTrailers.Services;

/// <summary>
/// Maps the library's YouTube trailer links to the items that own them.
///
/// Jellyfin's metadata providers (TMDb in particular) already store trailer
/// URLs on every movie/series as <c>BaseItem.RemoteTrailers</c>, and they are
/// almost always YouTube. That makes them the natural work-list for "cache
/// trailers for my library" — no extra scraping, no guessing, and the resulting
/// bundle is keyed by exactly the video ID a client will ask for.
///
/// The index is rebuilt lazily behind a short TTL because walking every item is
/// not free on a large library and the dashboard polls.
/// </summary>
public sealed class TrailerIndex
{
    private static readonly TimeSpan Ttl = TimeSpan.FromMinutes(5);

    private static readonly BaseItemKind[] TrailerBearingKinds =
    [
        BaseItemKind.Movie,
        BaseItemKind.Series,
        BaseItemKind.MusicVideo,
        BaseItemKind.Video,
    ];

    private readonly ILibraryManager _libraryManager;
    private readonly ILogger<TrailerIndex> _logger;
    private readonly object _sync = new();
    private IndexSnapshot? _cached;

    public TrailerIndex(ILibraryManager libraryManager, ILogger<TrailerIndex> logger)
    {
        _libraryManager = libraryManager;
        _logger = logger;
    }

    /// <summary>Returns the index, rebuilding it if stale or if forced.</summary>
    public IndexSnapshot Get(bool forceRebuild = false)
    {
        lock (_sync)
        {
            if (!forceRebuild && _cached is not null && DateTime.UtcNow - _cached.BuiltUtc < Ttl)
            {
                return _cached;
            }
            _cached = Build();
            return _cached;
        }
    }

    /// <summary>Human label for a video ID ("Dune: Part Two (2024)"), or null if the library doesn't reference it.</summary>
    public string? LabelFor(string videoId) =>
        Get().ByVideoId.TryGetValue(videoId, out var t) ? t.DisplayName : null;

    private IndexSnapshot Build()
    {
        var trailers = new List<LibraryTrailer>();
        var libraries = new List<LibrarySummary>();

        List<Folder> views;
        try
        {
            views = _libraryManager.GetUserRootFolder().Children?.OfType<Folder>().ToList() ?? [];
        }
        catch (Exception ex)
        {
            // Surface this rather than reporting an innocent-looking zero.
            // A failure here (e.g. a Jellyfin schema/migration mismatch making
            // its own item queries throw) is indistinguishable in the UI from
            // "you genuinely have no trailers", and the two need very different
            // responses from the admin.
            _logger.LogWarning(ex, "[YouTubeTrailers] could not enumerate libraries");
            return new IndexSnapshot(DateTime.UtcNow, [], new Dictionary<string, LibraryTrailer>(), [],
                Describe(ex));
        }

        string? firstError = null;
        foreach (var lib in views)
        {
            var libName = lib.Name ?? "(unnamed)";
            IReadOnlyList<BaseItem> items;
            try
            {
                items = _libraryManager.GetItemList(new InternalItemsQuery
                {
                    IncludeItemTypes = TrailerBearingKinds,
                    Recursive = true,
                    IsVirtualItem = false,
                    ParentId = lib.Id,
                });
            }
            catch (Exception ex)
            {
                // One unreadable library must not take out the whole index, and
                // must not surface as a 500 from the dashboard. Record it, skip
                // this library, and keep going — partial coverage beats none.
                _logger.LogWarning(ex, "[YouTubeTrailers] could not enumerate items in library {Library}", libName);
                firstError ??= $"{libName}: {Describe(ex)}";
                continue;
            }

            var itemsWithTrailers = 0;
            foreach (var item in items)
            {
                var remote = item.RemoteTrailers;
                if (remote is null || remote.Count == 0)
                {
                    continue;
                }
                var primary = true;
                var addedForItem = false;
                foreach (var url in remote)
                {
                    var videoId = YouTubeLink.ExtractId(url.Url);
                    if (videoId is null)
                    {
                        continue; // Vimeo and friends — nothing this plugin can serve.
                    }
                    trailers.Add(new LibraryTrailer(
                        item.Id,
                        item.Name ?? "(untitled)",
                        item.ProductionYear,
                        item.GetType().Name,
                        lib.Id,
                        libName,
                        videoId,
                        string.IsNullOrWhiteSpace(url.Name) ? null : url.Name,
                        primary));
                    primary = false;
                    addedForItem = true;
                }
                if (addedForItem)
                {
                    itemsWithTrailers++;
                }
            }

            libraries.Add(new LibrarySummary(lib.Id, libName, items.Count, itemsWithTrailers));
        }

        // First writer wins for a given video ID: two movies sharing a trailer
        // link is rare but possible (re-releases, franchise compilations), and
        // for labelling purposes either is fine.
        var byVideoId = new Dictionary<string, LibraryTrailer>(StringComparer.Ordinal);
        foreach (var t in trailers)
        {
            byVideoId.TryAdd(t.VideoId, t);
        }

        _logger.LogDebug(
            "[YouTubeTrailers] trailer index: {Trailers} YouTube link(s) across {Libraries} librar(ies)",
            trailers.Count, libraries.Count);

        return new IndexSnapshot(
            DateTime.UtcNow,
            trailers,
            byVideoId,
            libraries.OrderBy(l => l.Name, StringComparer.OrdinalIgnoreCase).ToList(),
            firstError);
    }

    /// <summary>Extracts the YouTube trailer IDs for a single item, primary first.</summary>
    public static IReadOnlyList<string> VideoIdsFor(BaseItem item)
    {
        var remote = item.RemoteTrailers;
        if (remote is null || remote.Count == 0)
        {
            return [];
        }
        var ids = new List<string>();
        foreach (var url in remote)
        {
            var id = YouTubeLink.ExtractId(url.Url);
            if (id is not null && !ids.Contains(id, StringComparer.Ordinal))
            {
                ids.Add(id);
            }
        }
        return ids;
    }

    public static string DisplayNameFor(BaseItem item) =>
        item.ProductionYear is > 0
            ? $"{item.Name} ({item.ProductionYear})"
            : item.Name ?? "(untitled)";

    /// <summary>Unwraps reflection wrappers so the admin sees the real cause, not "Exception has been thrown by the target of an invocation".</summary>
    private static string Describe(Exception ex)
    {
        var root = ex;
        while (root.InnerException is not null)
        {
            root = root.InnerException;
        }
        return $"{root.GetType().Name}: {root.Message}";
    }

    public sealed record IndexSnapshot(
        DateTime BuiltUtc,
        IReadOnlyList<LibraryTrailer> Trailers,
        IReadOnlyDictionary<string, LibraryTrailer> ByVideoId,
        IReadOnlyList<LibrarySummary> Libraries,
        string? Error);

    public sealed record LibraryTrailer(
        Guid ItemId,
        string ItemName,
        int? Year,
        string ItemType,
        Guid LibraryId,
        string LibraryName,
        string VideoId,
        string? TrailerName,
        bool IsPrimary)
    {
        public string DisplayName => Year is > 0 ? $"{ItemName} ({Year})" : ItemName;
    }

    public sealed record LibrarySummary(Guid Id, string Name, int TotalItems, int ItemsWithTrailers);
}
