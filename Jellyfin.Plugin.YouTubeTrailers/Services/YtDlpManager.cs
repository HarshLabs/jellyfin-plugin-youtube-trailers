using System;
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
            var tmp = ManagedPath + ".tmp";

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

            File.Move(tmp, ManagedPath, overwrite: true);

            // Validate it actually RUNS on this system. A glibc standalone won't
            // execute on musl (Alpine), and a wrong-arch build won't either — the
            // download succeeds but the binary is unusable. Surface that clearly
            // instead of letting every later resolve fail with a cryptic error.
            var version = await RunVersionAsync(ManagedPath, ct).ConfigureAwait(false);
            if (string.IsNullOrWhiteSpace(version))
            {
                try { File.Delete(ManagedPath); } catch { /* ignore */ }
                _logger.LogError(
                    "[YouTubeTrailers] Managed yt-dlp ({Asset}) downloaded but won't run on this system. "
                    + "On Alpine/musl Linux (or an unusual arch), install yt-dlp via your package manager "
                    + "and set its path in the plugin config.", asset);
                return (false, $"{asset} downloaded but won't run here (e.g. Alpine/musl). Install yt-dlp via your package manager and set its path.");
            }

            _logger.LogInformation("[YouTubeTrailers] Installed managed yt-dlp ({Asset}) {Version} → {Path}", asset, version, ManagedPath);
            return (true, $"Installed {asset} ({version}).");
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "[YouTubeTrailers] yt-dlp download failed");
            try { var tmp = ManagedPath + ".tmp"; if (File.Exists(tmp)) File.Delete(tmp); } catch { /* ignore */ }
            return (false, ex.Message);
        }
        finally
        {
            _downloadLock.Release();
        }
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
