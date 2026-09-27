using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Net.Http;
using System.Runtime.InteropServices;
using System.Threading;
using System.Threading.Tasks;
using MediaBrowser.Common.Configuration;
using Microsoft.Extensions.Logging;

namespace Jellyfin.Plugin.YouTubeTrailers.Services;

/// <summary>
/// Manages a plugin-owned copy of the official yt-dlp standalone binary so the
/// plugin works out of the box — no manual install, no Python. Downloads the
/// correct build for the server's OS/arch from yt-dlp's GitHub releases and
/// keeps it under the plugin's data folder. The configured <c>YtDlpPath</c>
/// (if it points at a real file) always wins, so admins can pin a system yt-dlp.
/// </summary>
public sealed class YtDlpManager
{
    private readonly IApplicationPaths _appPaths;
    private readonly IHttpClientFactory _httpClientFactory;
    private readonly ILogger<YtDlpManager> _logger;
    private readonly SemaphoreSlim _downloadLock = new(1, 1);

    // Self-heal: failures that point at a stale yt-dlp (see ReportStaleSignal).
    private readonly object _staleSync = new();
    private readonly List<DateTime> _staleSignals = new();
    private DateTime _lastAutoUpdateUtc = DateTime.MinValue;
    private const int StaleSignalThreshold = 3;
    private static readonly TimeSpan StaleSignalWindow = TimeSpan.FromMinutes(10);
    private static readonly TimeSpan AutoUpdateCooldown = TimeSpan.FromHours(1);

    /// <summary>
    /// Raised after a new managed yt-dlp is installed (task, self-heal, or the
    /// dashboard button), so dependants can drop state that the old binary
    /// caused — e.g. negative-cache entries for trailers it failed to build.
    /// </summary>
    public event Action? Updated;

    public YtDlpManager(IApplicationPaths appPaths, IHttpClientFactory httpClientFactory, ILogger<YtDlpManager> logger)
    {
        _appPaths = appPaths;
        _httpClientFactory = httpClientFactory;
        _logger = logger;
    }

    private string BinDir => Path.Combine(_appPaths.DataPath, "youtube-trailers");

    /// <summary>Path to the plugin-managed yt-dlp binary (may not exist yet).</summary>
    public string ManagedPath => Path.Combine(BinDir, OperatingSystem.IsWindows() ? "yt-dlp.exe" : "yt-dlp");

    /// <summary>
    /// The yt-dlp executable to use, in priority order: a configured path that
    /// actually exists, else the managed binary if downloaded, else null.
    /// </summary>
    public string? Resolve()
    {
        var configured = Plugin.Instance?.Configuration.YtDlpPath;
        if (!string.IsNullOrWhiteSpace(configured) && File.Exists(configured))
        {
            return configured;
        }
        return File.Exists(ManagedPath) ? ManagedPath : null;
    }

    public bool HasUsable => Resolve() is not null;

    /// <summary>True when the configured path resolves (admin pinned a system yt-dlp).</summary>
    public bool UsingConfigured
    {
        get
        {
            var configured = Plugin.Instance?.Configuration.YtDlpPath;
            return !string.IsNullOrWhiteSpace(configured) && File.Exists(configured);
        }
    }

    /// <summary>The GitHub release asset name for this server's OS/architecture.</summary>
    private static string AssetName()
    {
        if (OperatingSystem.IsWindows())
        {
            return "yt-dlp.exe";
        }
        if (OperatingSystem.IsMacOS())
        {
            return "yt-dlp_macos";
        }
        // Linux — pick by architecture (glibc standalone builds).
        return RuntimeInformation.OSArchitecture switch
        {
            Architecture.Arm64 => "yt-dlp_linux_aarch64",
            Architecture.Arm => "yt-dlp_linux_armv7l",
            _ => "yt-dlp_linux",
        };
    }

    /// <summary>
    /// Downloads (or re-downloads) the latest managed yt-dlp for this OS/arch.
    /// Atomic: writes to a temp file then moves into place. Returns success +
    /// a short human-readable message.
    /// </summary>
    public async Task<(bool Ok, string Message)> DownloadAsync(CancellationToken ct)
    {
        if (UsingConfigured)
        {
            return (true, "Using the configured yt-dlp path; managed download skipped.");
        }

        await _downloadLock.WaitAsync(ct).ConfigureAwait(false);
        try
        {
            Directory.CreateDirectory(BinDir);
            var asset = AssetName();
            var url = $"https://github.com/yt-dlp/yt-dlp/releases/latest/download/{asset}";
            // Keeps the .exe suffix on Windows so the candidate can be run for
            // validation before it replaces anything.
            var tmp = StagingPath;

            var client = _httpClientFactory.CreateClient();
            client.Timeout = TimeSpan.FromMinutes(5);
            using (var resp = await client.GetAsync(url, HttpCompletionOption.ResponseHeadersRead, ct).ConfigureAwait(false))
            {
                resp.EnsureSuccessStatusCode();
                await using var fs = new FileStream(tmp, FileMode.Create, FileAccess.Write, FileShare.None);
                await resp.Content.CopyToAsync(fs, ct).ConfigureAwait(false);
            }

            if (!OperatingSystem.IsWindows())
            {
                // Make it executable (rwxr-xr-x).
                File.SetUnixFileMode(tmp,
                    UnixFileMode.UserRead | UnixFileMode.UserWrite | UnixFileMode.UserExecute |
                    UnixFileMode.GroupRead | UnixFileMode.GroupExecute |
                    UnixFileMode.OtherRead | UnixFileMode.OtherExecute);
            }

            // Validate the download actually RUNS on this system BEFORE it
            // replaces anything. A glibc standalone won't execute on musl
            // (Alpine), and a wrong-arch build won't either — the download
            // succeeds but the binary is unusable. Checking the staged copy
            // means a bad update can never take away a working yt-dlp.
            var version = await RunVersionAsync(tmp, ct).ConfigureAwait(false);
            if (string.IsNullOrWhiteSpace(version))
            {
                try { File.Delete(tmp); } catch { /* ignore */ }
                _logger.LogError(
                    "[YouTubeTrailers] Managed yt-dlp ({Asset}) downloaded but won't run on this system. "
                    + "On Alpine/musl Linux (or an unusual arch), install yt-dlp via your package manager "
                    + "and set its path in the plugin config.", asset);
                return (false, $"{asset} downloaded but won't run here (e.g. Alpine/musl). Install yt-dlp via your package manager and set its path.");
            }

            await ReplaceManagedAsync(tmp, ct).ConfigureAwait(false);
            _logger.LogInformation("[YouTubeTrailers] Installed managed yt-dlp ({Asset}) {Version} → {Path}", asset, version, ManagedPath);
            try { Updated?.Invoke(); }
            catch (Exception ex) { _logger.LogWarning(ex, "[YouTubeTrailers] yt-dlp Updated handler failed"); }
            return (true, $"Installed {asset} ({version}).");
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "[YouTubeTrailers] yt-dlp download failed");
            try { if (File.Exists(StagingPath)) File.Delete(StagingPath); } catch { /* ignore */ }
            return (false, ex.Message);
        }
        finally
        {
            _downloadLock.Release();
        }
    }

    private string StagingPath => Path.Combine(BinDir, OperatingSystem.IsWindows() ? "yt-dlp.new.exe" : "yt-dlp.new");

    /// <summary>
    /// Moves the validated download over the managed binary. On Windows the
    /// target can be briefly locked by an in-flight resolve, so the move is
    /// retried for a few seconds before giving up (the old binary stays).
    /// </summary>
    private async Task ReplaceManagedAsync(string staged, CancellationToken ct)
    {
        for (var attempt = 1; ; attempt++)
        {
            try
            {
                File.Move(staged, ManagedPath, overwrite: true);
                return;
            }
            catch (IOException) when (attempt < 5)
            {
                await Task.Delay(TimeSpan.FromSeconds(2), ct).ConfigureAwait(false);
            }
            catch (UnauthorizedAccessException) when (attempt < 5)
            {
                await Task.Delay(TimeSpan.FromSeconds(2), ct).ConfigureAwait(false);
            }
        }
    }

    /// <summary>The version of the yt-dlp in use (configured or managed), or null.</summary>
    public async Task<string?> InstalledVersionAsync(CancellationToken ct)
    {
        var path = Resolve();
        return path is null ? null : await RunVersionAsync(path, ct).ConfigureAwait(false);
    }

    /// <summary>
    /// The latest stable yt-dlp release tag (e.g. "2026.08.19"), read from the
    /// redirect GitHub serves for /releases/latest — no API token or rate limit.
    /// Null when GitHub can't be reached.
    /// </summary>
    public async Task<string?> LatestVersionAsync(CancellationToken ct)
    {
        try
        {
            var client = _httpClientFactory.CreateClient();
            client.Timeout = TimeSpan.FromSeconds(30);
            using var request = new HttpRequestMessage(HttpMethod.Head, "https://github.com/yt-dlp/yt-dlp/releases/latest");
            using var response = await client.SendAsync(request, ct).ConfigureAwait(false);
            var path = response.RequestMessage?.RequestUri?.AbsolutePath ?? string.Empty;
            const string marker = "/releases/tag/";
            var index = path.LastIndexOf(marker, StringComparison.Ordinal);
            var tag = index < 0 ? string.Empty : path[(index + marker.Length)..].Trim('/');
            return tag.Length > 0 ? tag : null;
        }
        catch (Exception ex) when (ex is not OperationCanceledException || !ct.IsCancellationRequested)
        {
            _logger.LogWarning(ex, "[YouTubeTrailers] Couldn't check the latest yt-dlp version");
            return null;
        }
    }

    /// <summary>
    /// Updates the managed yt-dlp when a newer stable release exists. YouTube
    /// changes regularly break older yt-dlp builds (stream URLs start returning
    /// HTTP 403, or extraction fails), so an install that is never refreshed
    /// eventually fails every trailer. No-op when management is off or an
    /// admin pinned their own yt-dlp path.
    /// </summary>
    public async Task<(bool Updated, string Message)> UpdateIfOutdatedAsync(string reason, CancellationToken ct)
    {
        var cfg = Plugin.Instance?.Configuration;
        if (cfg is null || !cfg.ManageYtDlp)
        {
            return (false, "yt-dlp auto-management is off.");
        }
        if (UsingConfigured)
        {
            return (false, "Using the configured yt-dlp path; not updating it.");
        }

        var installed = await InstalledVersionAsync(ct).ConfigureAwait(false);
        var latest = await LatestVersionAsync(ct).ConfigureAwait(false);
        if (latest is null)
        {
            return (false, "Couldn't reach GitHub to check for a newer yt-dlp.");
        }
        if (string.Equals(installed, latest, StringComparison.Ordinal))
        {
            _logger.LogInformation("[YouTubeTrailers] yt-dlp {Version} is up to date ({Reason})", installed, reason);
            return (false, $"yt-dlp {installed} is up to date.");
        }

        _logger.LogInformation(
            "[YouTubeTrailers] Updating yt-dlp {Installed} → {Latest} ({Reason})",
            installed ?? "(none)", latest, reason);
        var result = await DownloadAsync(ct).ConfigureAwait(false);
        return (result.Ok, result.Message);
    }

    /// <summary>
    /// Records a failure whose signature points at a stale yt-dlp (googlevideo
    /// HTTP 403 on the stream URLs it resolved, or yt-dlp failing to resolve).
    /// Several within a few minutes trigger one update check — throttled to
    /// once an hour, and it only downloads when a newer release exists, so a
    /// genuinely blocked network or unavailable videos never loop.
    /// </summary>
    public void ReportStaleSignal(string signal)
    {
        var now = DateTime.UtcNow;
        lock (_staleSync)
        {
            _staleSignals.RemoveAll(t => now - t > StaleSignalWindow);
            _staleSignals.Add(now);
            if (_staleSignals.Count < StaleSignalThreshold || now - _lastAutoUpdateUtc < AutoUpdateCooldown)
            {
                return;
            }
            _lastAutoUpdateUtc = now;
            _staleSignals.Clear();
        }

        _logger.LogWarning(
            "[YouTubeTrailers] {Count}+ builds failed with {Signal} within {Minutes} min — checking for a newer yt-dlp",
            StaleSignalThreshold, signal, (int)StaleSignalWindow.TotalMinutes);
        _ = Task.Run(async () =>
        {
            try
            {
                await UpdateIfOutdatedAsync($"self-heal after repeated {signal}", CancellationToken.None).ConfigureAwait(false);
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "[YouTubeTrailers] yt-dlp self-heal update failed");
            }
        });
    }

    /// <summary>Runs the binary with <c>--version</c>; returns the version string or null if it won't run.</summary>
    private static async Task<string?> RunVersionAsync(string path, CancellationToken ct)
    {
        try
        {
            var psi = new ProcessStartInfo
            {
                FileName = path,
                RedirectStandardOutput = true,
                RedirectStandardError = true,
                UseShellExecute = false,
            };
            psi.ArgumentList.Add("--version");
            using var process = Process.Start(psi);
            if (process is null)
            {
                return null;
            }
            var stdoutTask = process.StandardOutput.ReadToEndAsync(ct);
            using var timeoutCts = CancellationTokenSource.CreateLinkedTokenSource(ct);
            timeoutCts.CancelAfter(TimeSpan.FromSeconds(15));
            await process.WaitForExitAsync(timeoutCts.Token).ConfigureAwait(false);
            var output = (await stdoutTask.ConfigureAwait(false)).Trim();
            return process.ExitCode == 0 && output.Length > 0 ? output : null;
        }
        catch
        {
            return null;
        }
    }

    /// <summary>
    /// Ensures a usable yt-dlp exists when auto-management is on — downloads the
    /// managed binary if nothing is available yet. No-op when a configured or
    /// managed binary is already present, or management is disabled.
    /// </summary>
    public async Task EnsureAsync(CancellationToken ct)
    {
        var cfg = Plugin.Instance?.Configuration;
        if (cfg is null || !cfg.ManageYtDlp || HasUsable)
        {
            return;
        }
        _logger.LogInformation("[YouTubeTrailers] No yt-dlp found — downloading managed binary…");
        await DownloadAsync(ct).ConfigureAwait(false);
    }
}
