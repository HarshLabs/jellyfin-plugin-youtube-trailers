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

    /// <summary>
    /// When true (default), the plugin downloads and maintains its own yt-dlp
    /// standalone binary (no manual install / Python needed) under its data
    /// folder. A non-empty <see cref="YtDlpPath"/> that exists always overrides.
    /// </summary>
    public bool ManageYtDlp { get; set; } = true;

    /// <summary>
    /// Absolute path to a system yt-dlp binary. Leave blank to use the
    /// plugin-managed binary (recommended). When set and present, it overrides
    /// the managed one.
    /// </summary>
    public string YtDlpPath { get; set; } = string.Empty;

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

    /// <summary>
    /// Trailer quality, as a friendly preset rather than raw yt-dlp syntax:
    /// <c>best</c>, <c>1080p</c>, <c>720p</c>, <c>480p</c>, or <c>custom</c> to
    /// hand-write <see cref="FormatSelector"/>.
    ///
    /// Every preset pins H.264 + AAC on purpose. That is the combination Apple
    /// devices decode in hardware AND the one that can be stream-copied straight
    /// into fMP4 — pick VP9/AV1/Opus and the "no re-encode" promise (and the
    /// speed that comes with it) is gone.
    /// </summary>
    public string QualityPreset { get; set; } = "1080p";

    /// <summary>
    /// Raw yt-dlp format selector. Only consulted when
    /// <see cref="QualityPreset"/> is <c>custom</c>; otherwise the preset
    /// generates it. See <see cref="Services.ToolRunner.FormatSelectorFor"/>.
    /// </summary>
    public string FormatSelector { get; set; } =
        "bestvideo[height<=1080][vcodec^=avc1]+bestaudio[acodec^=mp4a]/best[ext=mp4]/best";

    /// <summary>
    /// HTTP/HTTPS/SOCKS proxy URL (e.g. <c>http://host:port</c> or
    /// <c>socks5://host:port</c>) applied to BOTH yt-dlp (resolution) AND ffmpeg
    /// (the actual video fetch). Use this — not a bare <c>--proxy</c> in
    /// <see cref="YtDlpArguments"/> — for geo-blocked trailers, since the fetch
    /// must also be proxied. Empty = direct connection.
    /// </summary>
    public string Proxy { get; set; } = string.Empty;

    /// <summary>
    /// Extra space-separated arguments passed to every yt-dlp invocation.
    /// Pro escape hatch for: <c>--cookies FILE</c> / <c>--cookies-from-browser B</c>
    /// (age-restricted or bot-checked videos), <c>--extractor-args</c> (PO
    /// tokens), <c>--limit-rate</c>, etc. For a proxy use the dedicated Proxy
    /// setting (it also proxies ffmpeg). Empty by default. Admin-only input.
    /// </summary>
    public string YtDlpArguments { get; set; } = string.Empty;

    /// <summary>
    /// Browser to lift YouTube cookies from (<c>chrome</c>, <c>firefox</c>,
    /// <c>edge</c>, <c>safari</c>, <c>brave</c>, <c>chromium</c>, <c>opera</c>,
    /// <c>vivaldi</c>). Empty = don't.
    ///
    /// Promoted to a first-class setting because "Sign in to confirm you're not
    /// a bot" is by far the most common way trailer extraction fails, and the
    /// fix is always cookies. Requires a browser profile on the *server*, which
    /// headless/Docker installs won't have — those should use
    /// <see cref="CookiesFile"/> instead.
    /// </summary>
    public string CookiesFromBrowser { get; set; } = string.Empty;

    /// <summary>
    /// Absolute path to a Netscape-format cookies.txt exported from a signed-in
    /// browser. Takes precedence over <see cref="CookiesFromBrowser"/> and is
    /// the option that works on headless servers and in containers.
    /// </summary>
    public string CookiesFile { get; set; } = string.Empty;

    /// <summary>Hard ceiling (seconds) on a single resolve+remux before giving up.</summary>
    public int ResolveTimeoutSeconds { get; set; } = 60;

    /// <summary>
    /// Max trailers built concurrently (server-side ffmpeg/yt-dlp jobs). Raise on
    /// a powerful server, lower on a weak one. Clamped 1–16. Applied live — the
    /// build-slot pool resizes when this is saved, no restart needed.
    /// </summary>
    public int MaxConcurrentBuilds { get; set; } = 4;

    /// <summary>
    /// Caps how fast a build reads from YouTube, as a multiple of realtime
    /// (2 = twice playback speed). 0 = unlimited.
    ///
    /// This is applied to <b>ffmpeg</b>, not yt-dlp, because ffmpeg is what
    /// actually downloads the video — yt-dlp only resolves the URL here, so
    /// yt-dlp's own <c>--limit-rate</c> would do nothing. Useful to stop a
    /// library-wide sweep saturating a slow uplink. Values below 1 are refused
    /// (the build would fall behind playback and stall the no-segment watchdog).
    /// </summary>
    public double BuildSpeedLimit { get; set; } = 0;

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

    // ── Automatic trailers for new media ────────────────────────────────────

    /// <summary>
    /// When true, every newly added (or newly re-scanned) movie/series has its
    /// YouTube trailer built and cached in the background, so it plays instantly
    /// the first time anyone opens the item.
    ///
    /// Default off: a first library scan fires thousands of add events and each
    /// trailer is a real yt-dlp + ffmpeg job against YouTube. Turn it on once
    /// <see cref="MaxConcurrentBuilds"/> is sized for your server.
    /// </summary>
    public bool PrewarmOnLibraryAdd { get; set; } = false;

    /// <summary>
    /// How long to wait after an item is added before looking for its trailer
    /// link. Jellyfin raises ItemAdded when the file is discovered — *before*
    /// the metadata provider attaches RemoteTrailers — so resolving immediately
    /// would find nothing on a fresh import. Two minutes is enough for a TMDb
    /// lookup on a normal connection.
    /// </summary>
    public int LibraryAddDelaySeconds { get; set; } = 120;

    /// <summary>
    /// Items often carry several trailers (teaser, official trailer, clip).
    /// By default only the first is cached — that's the one clients play.
    /// Enable to cache every YouTube trailer on the item instead.
    /// </summary>
    public bool PrewarmAllTrailersPerItem { get; set; } = false;

    /// <summary>
    /// Enables the daily scheduled sweep that caches trailers for every library
    /// item that has a YouTube link and isn't cached yet. Backstop for items
    /// imported while the live listener was off, or whose metadata arrived late.
    /// </summary>
    public bool EnableLibraryPrewarmTask { get; set; } = false;

    /// <summary>
    /// Restricts automatic caching (both the library-add listener and the daily
    /// sweep) to these library IDs. Empty = every library.
    ///
    /// Worth setting on a server where only some libraries have trailers worth
    /// caching — a Home Videos or Music Videos library will happily burn build
    /// slots on links nobody will ever play.
    /// </summary>
    public string[] PrewarmLibraryIds { get; set; } = [];

    /// <summary>
    /// Maximum trailers the scheduled sweep builds in one run. Keeps the first
    /// run on a large library from turning into an all-night YouTube crawl.
    /// 0 = no cap.
    /// </summary>
    public int MaxPrewarmPerRun { get; set; } = 200;

    // ── Diagnostics / tuning ────────────────────────────────────────────────

    /// <summary>
    /// How long a failed video keeps fast-failing before it's retried. This is
    /// what makes a client's "try the next trailer" fallback instant instead of
    /// re-running the full timeout. 0 disables the negative cache (every request
    /// retries — useful while debugging a specific video).
    /// </summary>
    public int FailureCacheMinutes { get; set; } = 10;

    /// <summary>
    /// Kill a build that hasn't produced its first segment within this many
    /// seconds. A dead CDN edge makes ffmpeg's reconnect logic retry forever
    /// without ever exiting, so without this the client waits out the full
    /// resolve timeout instead of falling back to the next trailer.
    /// </summary>
    public int NoSegmentTimeoutSeconds { get; set; } = 20;

    /// <summary>
    /// Grace period the full-screen (<c>?complete=1</c>) path waits for a remux
    /// to finish so AVKit gets a finite VOD playlist (real scrubber) rather than
    /// the live UI. Deliberately small — on timeout playback starts live anyway.
    /// </summary>
    public int CompleteWaitSeconds { get; set; } = 3;

    /// <summary>
    /// Logs the full yt-dlp / ffmpeg command line for every invocation at Info
    /// level. Noisy, but it's the fastest way to see exactly what the plugin ran
    /// when a trailer misbehaves on a specific server.
    /// </summary>
    public bool VerboseLogging { get; set; } = false;
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
        serviceCollection.AddSingleton<YtDlpManager>();
        // Shared yt-dlp/ffmpeg plumbing. Both the build path and the diagnose
        // path go through it, so "diagnose" runs the very same commands a real
        // build runs — otherwise it wouldn't be diagnosing anything.
        serviceCollection.AddSingleton<ToolRunner>();
        serviceCollection.AddSingleton<TrailerDiagnostics>();
        serviceCollection.AddSingleton<TrailerResolver>();
        // Maps library items ↔ their YouTube trailer links (from the metadata
        // providers' RemoteTrailers), which is what makes coverage stats and
        // library-wide prewarming possible.
        serviceCollection.AddSingleton<TrailerIndex>();
        serviceCollection.AddSingleton<PrewarmQueue>();
        serviceCollection.AddHostedService(sp => sp.GetRequiredService<PrewarmQueue>());

        serviceCollection.AddSingleton<IScheduledTask, Tasks.PruneCacheTask>();
        serviceCollection.AddSingleton<IScheduledTask, Tasks.PrewarmLibraryTrailersTask>();

        serviceCollection.AddHostedService<YtDlpBootstrapService>();
        // Optional (config-gated): cache a trailer whenever the scanner adds or
        // refreshes an item, so new media is warm before anyone opens it.
        serviceCollection.AddHostedService<LibraryAddListener>();
    }
}
