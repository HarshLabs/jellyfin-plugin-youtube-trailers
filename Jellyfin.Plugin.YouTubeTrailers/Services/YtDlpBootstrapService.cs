using System.Threading;
using System.Threading.Tasks;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;

namespace Jellyfin.Plugin.YouTubeTrailers.Services;

/// <summary>
/// On server start, ensures a usable yt-dlp is present (downloads the managed
/// standalone binary if none is found and auto-management is enabled). Runs in
/// the background so it never blocks Jellyfin startup.
/// </summary>
public sealed class YtDlpBootstrapService : IHostedService
{
    private readonly YtDlpManager _ytDlp;
    private readonly ILogger<YtDlpBootstrapService> _logger;

    public YtDlpBootstrapService(YtDlpManager ytDlp, ILogger<YtDlpBootstrapService> logger)
    {
        _ytDlp = ytDlp;
        _logger = logger;
    }

    public Task StartAsync(CancellationToken cancellationToken)
    {
        // Detached — must not delay host startup. CancellationToken.None so a
        // slow download isn't aborted when the startup phase completes.
        _ = Task.Run(async () =>
        {
            try
            {
                await _ytDlp.EnsureAsync(CancellationToken.None).ConfigureAwait(false);
            }
            catch (System.Exception ex)
            {
                _logger.LogError(ex, "[YouTubeTrailers] yt-dlp bootstrap failed");
            }
        });
        return Task.CompletedTask;
    }

    public Task StopAsync(CancellationToken cancellationToken) => Task.CompletedTask;
}
