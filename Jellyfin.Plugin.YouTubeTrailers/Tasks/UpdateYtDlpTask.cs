using System;
using System.Collections.Generic;
using System.Threading;
using System.Threading.Tasks;
using Jellyfin.Plugin.YouTubeTrailers.Services;
using MediaBrowser.Model.Tasks;

namespace Jellyfin.Plugin.YouTubeTrailers.Tasks;

/// <summary>
/// Keeps the managed yt-dlp current. YouTube changes break older yt-dlp builds
/// every few weeks (stream URLs start returning HTTP 403), and the plugin only
/// ever downloaded yt-dlp when none was installed — so every server eventually
/// failed every trailer until an admin pressed "Download / update yt-dlp".
/// Checks GitHub for a newer stable release and installs it only when there is
/// one; a no-op when auto-management is off or a custom path is configured.
/// </summary>
public sealed class UpdateYtDlpTask : IScheduledTask
{
    private readonly YtDlpManager _ytDlp;

    public UpdateYtDlpTask(YtDlpManager ytDlp)
    {
        _ytDlp = ytDlp;
    }

    public string Name => "Update yt-dlp";
    public string Key => "YouTubeTrailersUpdateYtDlp";
    public string Description => "Installs the latest stable yt-dlp when a newer release exists, so YouTube changes don't break trailer downloads.";
    public string Category => "YouTube Trailers";

    public IEnumerable<TaskTriggerInfo> GetDefaultTriggers() =>
    [
        // Daily at ~2 AM, ahead of the 3 AM library prewarm so that sweep runs
        // on the fresh binary.
        new TaskTriggerInfo
        {
            Type = TaskTriggerInfoType.DailyTrigger,
            TimeOfDayTicks = TimeSpan.FromHours(2).Ticks
        }
    ];

    public async Task ExecuteAsync(IProgress<double> progress, CancellationToken cancellationToken)
    {
        await _ytDlp.UpdateIfOutdatedAsync("scheduled task", cancellationToken).ConfigureAwait(false);
        progress.Report(100);
    }
}
