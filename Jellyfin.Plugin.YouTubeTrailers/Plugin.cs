using System;
using System.Collections.Generic;
using MediaBrowser.Common.Configuration;
using MediaBrowser.Common.Plugins;
using MediaBrowser.Controller;
using MediaBrowser.Controller.Plugins;
using MediaBrowser.Model.Plugins;
using MediaBrowser.Model.Serialization;
using MediaBrowser.Model.Tasks;
using Microsoft.Extensions.DependencyInjection;
using Jellyfin.Plugin.YouTubeTrailers.Services;

namespace Jellyfin.Plugin.YouTubeTrailers;

public class PluginConfiguration : BasePluginConfiguration
{
    public bool Enabled { get; set; } = true;

    /// <summary>Absolute path to the yt-dlp binary on the server host.</summary>
    public string YtDlpPath { get; set; } = "/opt/homebrew/bin/yt-dlp";

    /// <summary>
    /// Override path to ffmpeg. Empty = use the server's bundled ffmpeg
    /// (IMediaEncoder.EncoderPath), which is the correct default.
    /// </summary>
    public string FfmpegPath { get; set; } = string.Empty;

    /// <summary>
    /// Cache directory for remuxed HLS. Empty = a "youtube-trailers" folder
    /// under Jellyfin's cache path.
    /// </summary>
    public string CacheDirectory { get; set; } = string.Empty;

    /// <summary>yt-dlp format selector. Prefers 1080p AVC1 + AAC, muxed fallback.</summary>
    public string FormatSelector { get; set; } =
        "bestvideo[height<=1080][vcodec^=avc1]+bestaudio[acodec^=mp4a]/best[ext=mp4]/best";

    /// <summary>
    /// Extra space-separated arguments passed to every yt-dlp invocation.
    /// Pro escape hatch for: <c>--proxy URL</c> (geo-blocked trailers),
    /// <c>--cookies FILE</c> / <c>--cookies-from-browser B</c> (age-restricted or
    /// bot-checked videos), <c>--extractor-args</c> (PO tokens),
    /// <c>--limit-rate</c>, etc. Empty by default. Admin-only input.
    /// </summary>
    public string YtDlpArguments { get; set; } = string.Empty;

    /// <summary>Hard ceiling (seconds) on a single resolve+remux before giving up.</summary>
    public int ResolveTimeoutSeconds { get; set; } = 60;

    /// <summary>
    /// Max trailers built concurrently (server-side ffmpeg/yt-dlp jobs). Raise on
    /// a powerful server, lower on a weak one. Clamped 1–16. Applied at plugin
    /// load — change requires a server restart.
    /// </summary>
    public int MaxConcurrentBuilds { get; set; } = 4;

    /// <summary>
    /// Evict cached trailer bundles older than this many days (by last access).
    /// 0 = no age limit. Applied by the daily prune task.
    /// </summary>
    public int MaxAgeDays { get; set; } = 30;

    /// <summary>
    /// Cap total cache size in gigabytes; the daily prune task evicts the
    /// least-recently-used bundles until under the cap. 0 = no size limit.
    /// </summary>
    public int MaxCacheGigabytes { get; set; } = 5;
}

public class Plugin : BasePlugin<PluginConfiguration>, IHasWebPages
{
    public Plugin(IApplicationPaths applicationPaths, IXmlSerializer xmlSerializer)
        : base(applicationPaths, xmlSerializer)
    {
        Instance = this;
    }

    public static Plugin? Instance { get; private set; }

    public override string Name => "YouTube Trailers";

    public override Guid Id => Guid.Parse("00e99003-cf35-4a65-bf44-35104dfeb76a");

    public override string Description =>
        "Resolves YouTube trailers server-side (yt-dlp) and stream-copy remuxes them to "
        + "AVPlayer-native fMP4 HLS, so tvOS clients play one clean URL with zero on-device extraction.";

    public IEnumerable<PluginPageInfo> GetPages() =>
    [
        new PluginPageInfo
        {
            Name = Name,
            EmbeddedResourcePath = GetType().Namespace + ".Configuration.configPage.html"
        }
    ];
}

public class PluginServiceRegistrator : IPluginServiceRegistrator
{
    public void RegisterServices(IServiceCollection serviceCollection, IServerApplicationHost applicationHost)
    {
        serviceCollection.AddSingleton<TrailerResolver>();
        serviceCollection.AddSingleton<IScheduledTask, Tasks.PruneCacheTask>();
    }
}
