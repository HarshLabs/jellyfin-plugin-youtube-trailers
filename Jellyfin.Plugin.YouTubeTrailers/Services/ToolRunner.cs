using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Linq;
using System.Text;
using System.Threading;
using System.Threading.Tasks;
using MediaBrowser.Controller.MediaEncoding;
using Microsoft.Extensions.Logging;

namespace Jellyfin.Plugin.YouTubeTrailers.Services;

/// <summary>
/// Shared plumbing for shelling out to yt-dlp and ffmpeg: binary resolution,
/// argument assembly (proxy + admin extra args), process execution, and
/// human-readable command rendering for the diagnostics UI.
///
/// Exists as its own service so <see cref="TrailerResolver"/> (which builds
/// trailers) and <see cref="TrailerDiagnostics"/> (which explains why a build
/// failed) can share it without depending on each other — the diagnose path
/// must be able to run the exact same commands the build path runs, otherwise
/// it isn't diagnosing the real thing.
/// </summary>
public sealed class ToolRunner
{
    private readonly IMediaEncoder _mediaEncoder;
    private readonly YtDlpManager _ytDlp;
    private readonly ILogger<ToolRunner> _logger;

    public ToolRunner(IMediaEncoder mediaEncoder, YtDlpManager ytDlp, ILogger<ToolRunner> logger)
    {
        _mediaEncoder = mediaEncoder;
        _ytDlp = ytDlp;
        _logger = logger;
    }

    public string? ResolveYtDlp() => _ytDlp.Resolve();

    /// <summary>
    /// Resolves a usable ffmpeg. Jellyfin's EncoderPath is often the bare name
    /// "ffmpeg" (resolved via PATH at launch), which File.Exists can't validate
    /// — so prefer absolute candidates that exist, falling back to a bare name
    /// (Process resolves it via PATH) only as a last resort.
    /// </summary>
    public string? ResolveFfmpeg()
    {
        var configured = Plugin.Instance?.Configuration.FfmpegPath;
        string?[] candidates =
        {
            string.IsNullOrWhiteSpace(configured) ? null : configured,
            _mediaEncoder.EncoderPath,
            "/opt/homebrew/bin/ffmpeg",
            "/usr/local/bin/ffmpeg",
            "/usr/bin/ffmpeg",
        };
        foreach (var c in candidates)
        {
            if (!string.IsNullOrWhiteSpace(c) && c.Contains('/') && File.Exists(c))
            {
                return c;
            }
        }
        foreach (var c in candidates)
        {
            if (!string.IsNullOrWhiteSpace(c))
            {
                return c;
            }
        }
        return null;
    }

    /// <summary>The raw EncoderPath Jellyfin reports, for the diagnostics view.</summary>
    public string EncoderPath => _mediaEncoder.EncoderPath ?? string.Empty;

    /// <summary>
    /// Builds a yt-dlp invocation carrying the plugin's standard flags plus the
    /// admin's configured proxy and extra arguments, so every caller (resolve,
    /// probe, diagnose) hits YouTube with an identical configuration.
    /// </summary>
    public ProcessStartInfo? YtDlpPsi(params string[] extraArgs)
    {
        var path = ResolveYtDlp();
        if (path is null)
        {
            return null;
        }
        var cfg = Plugin.Instance?.Configuration;
        var psi = new ProcessStartInfo
        {
            FileName = path,
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            UseShellExecute = false,
        };
        psi.ArgumentList.Add("--no-warnings");
        psi.ArgumentList.Add("--no-playlist");
        if (cfg is not null && !string.IsNullOrWhiteSpace(cfg.Proxy))
        {
            psi.ArgumentList.Add("--proxy");
            psi.ArgumentList.Add(cfg.Proxy);
        }
        // Cookies: a file wins over a browser profile, because the file is the
        // option that actually works on headless servers and in containers.
        if (cfg is not null && !string.IsNullOrWhiteSpace(cfg.CookiesFile))
        {
            psi.ArgumentList.Add("--cookies");
            psi.ArgumentList.Add(cfg.CookiesFile);
        }
        else if (cfg is not null && !string.IsNullOrWhiteSpace(cfg.CookiesFromBrowser))
        {
            psi.ArgumentList.Add("--cookies-from-browser");
            psi.ArgumentList.Add(cfg.CookiesFromBrowser);
        }
        // Admin free-form args go last so they can override anything above.
        foreach (var arg in SplitArgs(cfg?.YtDlpArguments))
        {
            psi.ArgumentList.Add(arg);
        }
        foreach (var arg in extraArgs)
        {
            psi.ArgumentList.Add(arg);
        }
        return psi;
    }

    /// <summary>
    /// Splits a configured argument string into individual args, honoring simple
    /// double-quotes so values with spaces (e.g. a cookies path) stay intact.
    /// </summary>
    public static IEnumerable<string> SplitArgs(string? args)
    {
        if (string.IsNullOrWhiteSpace(args))
        {
            yield break;
        }
        var sb = new StringBuilder();
        var inQuotes = false;
        foreach (var c in args)
        {
            if (c == '"')
            {
                inQuotes = !inQuotes;
            }
            else if (char.IsWhiteSpace(c) && !inQuotes)
            {
                if (sb.Length > 0) { yield return sb.ToString(); sb.Clear(); }
            }
            else
            {
                sb.Append(c);
            }
        }
        if (sb.Length > 0) { yield return sb.ToString(); }
    }

    /// <summary>
    /// Renders a command line for the admin UI / verbose log. Signed googlevideo
    /// URLs are truncated: they're 1-2 KB of query string, they leak the
    /// server's IP binding, and the host + itag is the only part that helps
    /// when reading a failure.
    /// </summary>
    public static string Describe(ProcessStartInfo psi)
    {
        var parts = new List<string> { Quote(Path.GetFileName(psi.FileName)) };
        foreach (var a in psi.ArgumentList)
        {
            parts.Add(Quote(Shorten(a)));
        }
        return string.Join(' ', parts);

        static string Quote(string s) => s.Contains(' ', StringComparison.Ordinal) ? $"\"{s}\"" : s;

        static string Shorten(string a)
        {
            if (a.Length <= 120 || !a.StartsWith("http", StringComparison.OrdinalIgnoreCase))
            {
                return a;
            }
            var q = a.IndexOf('?', StringComparison.Ordinal);
            var head = q > 0 ? a[..q] : a[..120];
            return head + "?…(" + a.Length + " chars)";
        }
    }

    /// <summary>Runs a process to completion, capturing stdout/stderr. Kills the tree on cancellation.</summary>
    public static async Task<(int Exit, string Stdout, string Stderr)> RunAsync(
        ProcessStartInfo psi, CancellationToken ct)
    {
        using var process = new Process { StartInfo = psi, EnableRaisingEvents = true };
        var stdout = new StringBuilder();
        var stderr = new StringBuilder();
        process.OutputDataReceived += (_, e) => { if (e.Data is not null) stdout.AppendLine(e.Data); };
        process.ErrorDataReceived += (_, e) => { if (e.Data is not null) stderr.AppendLine(e.Data); };

        process.Start();
        process.BeginOutputReadLine();
        process.BeginErrorReadLine();
        try
        {
            await process.WaitForExitAsync(ct).ConfigureAwait(false);
        }
        catch (OperationCanceledException)
        {
            try { if (!process.HasExited) process.Kill(entireProcessTree: true); } catch { /* best effort */ }
            throw;
        }
        return (process.ExitCode, stdout.ToString(), stderr.ToString());
    }

    /// <summary>Runs a process with a wall-clock cap, returning a timeout marker instead of throwing.</summary>
    public async Task<(int Exit, string Stdout, string Stderr, bool TimedOut)> RunWithTimeoutAsync(
        ProcessStartInfo psi, TimeSpan timeout, CancellationToken ct)
    {
        if (Plugin.Instance?.Configuration.VerboseLogging == true)
        {
            _logger.LogInformation("[YouTubeTrailers] exec: {Command}", Describe(psi));
        }
        using var cts = CancellationTokenSource.CreateLinkedTokenSource(ct);
        cts.CancelAfter(timeout);
        try
        {
            var (exit, stdout, stderr) = await RunAsync(psi, cts.Token).ConfigureAwait(false);
            return (exit, stdout, stderr, false);
        }
        catch (OperationCanceledException) when (!ct.IsCancellationRequested)
        {
            return (-1, string.Empty, $"timed out after {timeout.TotalSeconds:0}s", true);
        }
    }

    /// <summary>
    /// Result of asking yt-dlp to resolve a video: the direct media URL(s) for
    /// the configured format selector plus the metadata the UI needs (title for
    /// labelling a job, duration so ffmpeg progress can be turned into a
    /// percentage). Carries the failure detail too, so callers can record a
    /// useful diagnostic instead of a bare "returned no URLs".
    /// </summary>
    public sealed record ResolvedVideo(
        string[] Urls,
        double? DurationSeconds,
        string? Title,
        int Exit,
        string Stderr,
        string Command,
        bool TimedOut)
    {
        public bool Ok => Urls.Length is 1 or 2;
    }

    /// <summary>
    /// Runs the production resolve: the admin's format selector, plus title and
    /// duration in the same invocation (one yt-dlp round trip, not three).
    /// Output is parsed positionally-but-defensively — any line that looks like
    /// a URL is a media URL, everything else fills the metadata slots in the
    /// order the --print flags were given.
    /// </summary>
    /// <summary>
    /// Turns the friendly quality preset into a yt-dlp format selector.
    ///
    /// Every preset pins <c>avc1</c> video + <c>mp4a</c> audio deliberately: those
    /// are what Apple hardware decodes and, crucially, what can be stream-copied
    /// into fMP4 without re-encoding. Allowing VP9/AV1/Opus here would silently
    /// turn a ~0-CPU remux into a full transcode. Each preset still falls back
    /// through <c>best[ext=mp4]</c> to <c>best</c> so an oddly-encoded video
    /// still produces something rather than failing outright.
    /// </summary>
    public static string FormatSelectorFor(PluginConfiguration? cfg)
    {
        var preset = (cfg?.QualityPreset ?? "1080p").Trim().ToLowerInvariant();
        if (preset == "custom")
        {
            return string.IsNullOrWhiteSpace(cfg?.FormatSelector)
                ? HeightCappedSelector(1080)
                : cfg!.FormatSelector;
        }
        return preset switch
        {
            "best" => "bestvideo[vcodec^=avc1]+bestaudio[acodec^=mp4a]/best[ext=mp4]/best",
            "720p" => HeightCappedSelector(720),
            "480p" => HeightCappedSelector(480),
            _ => HeightCappedSelector(1080),
        };
    }

    private static string HeightCappedSelector(int height) =>
        $"bestvideo[height<={height}][vcodec^=avc1]+bestaudio[acodec^=mp4a]/best[ext=mp4]/best";

    public async Task<ResolvedVideo> ResolveVideoAsync(string videoId, CancellationToken ct)
    {
        var cfg = Plugin.Instance?.Configuration;
        var selector = FormatSelectorFor(cfg);

        var psi = YtDlpPsi(
            "-f", selector,
            "--print", "%(duration)j",
            "--print", "%(title)j",
            "--print", "%(urls)s",
            $"https://www.youtube.com/watch?v={videoId}");
        if (psi is null)
        {
            return new ResolvedVideo([], null, null, -1,
                "no usable yt-dlp (configured path missing and managed binary not installed)",
                "yt-dlp (not found)", false);
        }

        var command = Describe(psi);
        var (exit, stdout, stderr, timedOut) =
            await RunWithTimeoutAsync(psi, TimeSpan.FromSeconds(30), ct).ConfigureAwait(false);

        var urls = new List<string>();
        var meta = new List<string>();
        foreach (var raw in stdout.Split('\n', StringSplitOptions.RemoveEmptyEntries))
        {
            var line = raw.Trim();
            if (line.Length == 0)
            {
                continue;
            }
            if (line.StartsWith("http://", StringComparison.OrdinalIgnoreCase)
                || line.StartsWith("https://", StringComparison.OrdinalIgnoreCase))
            {
                urls.Add(line);
            }
            else
            {
                meta.Add(line);
            }
        }

        double? duration = meta.Count > 0 ? ParseJsonNumber(meta[0]) : null;
        string? title = meta.Count > 1 ? ParseJsonString(meta[1]) : null;
        return new ResolvedVideo(urls.ToArray(), duration, title, exit, stderr, command, timedOut);
    }

    /// <summary>Parses a <c>%(field)j</c> value as a number; yt-dlp emits bare <c>null</c> / <c>NA</c> for missing fields.</summary>
    public static double? ParseJsonNumber(string s)
    {
        s = s.Trim().Trim('"');
        if (s.Length == 0 || s is "null" or "NA" or "none")
        {
            return null;
        }
        return double.TryParse(s, System.Globalization.NumberStyles.Float,
            System.Globalization.CultureInfo.InvariantCulture, out var d) ? d : null;
    }

    /// <summary>Parses a <c>%(field)j</c> value as a string, unwrapping the JSON quoting/escapes.</summary>
    public static string? ParseJsonString(string s)
    {
        s = s.Trim();
        if (s.Length == 0 || s is "null" or "NA" or "none")
        {
            return null;
        }
        if (s.StartsWith('"'))
        {
            try
            {
                return System.Text.Json.JsonSerializer.Deserialize<string>(s);
            }
            catch (System.Text.Json.JsonException)
            {
                // Fall through to the raw value — a malformed line is still
                // better shown than swallowed.
            }
        }
        return s;
    }

    /// <summary>
    /// Result of asking a binary for its version. <see cref="Version"/> is null
    /// when the probe didn't produce one; <see cref="Detail"/> always says why
    /// in words an admin can act on.
    /// </summary>
    public sealed record VersionProbe(string? Version, string Detail)
    {
        public bool Ok => Version is not null;
    }

    /// <summary>
    /// Runs <c>&lt;binary&gt; --version</c> (or equivalent) and returns the first
    /// output line.
    ///
    /// Explicitly does NOT collapse every failure into "error": a missing file,
    /// a wrong-architecture binary, a slow first start, and a non-zero exit all
    /// need different fixes, and "error" in the dashboard sent an admin looking
    /// at the wrong thing. The timeout is generous because self-extracting
    /// standalone builds (yt-dlp's PyInstaller binaries) unpack on first run and
    /// can take several seconds on a cold cache.
    /// </summary>
    public static async Task<VersionProbe> ProbeVersionAsync(
        string exe, string[] args, TimeSpan timeout, CancellationToken ct)
    {
        if (exe.Contains(Path.DirectorySeparatorChar) && !File.Exists(exe))
        {
            return new VersionProbe(null, $"{exe} does not exist.");
        }
        var psi = new ProcessStartInfo
        {
            FileName = exe,
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            UseShellExecute = false,
        };
        foreach (var a in args)
        {
            psi.ArgumentList.Add(a);
        }

        try
        {
            using var cts = CancellationTokenSource.CreateLinkedTokenSource(ct);
            cts.CancelAfter(timeout);
            var (exit, stdout, stderr) = await RunAsync(psi, cts.Token).ConfigureAwait(false);
            var first = stdout.Split('\n', StringSplitOptions.RemoveEmptyEntries)
                .Select(l => l.Trim()).FirstOrDefault(l => l.Length > 0);

            if (exit == 0 && first is not null)
            {
                return new VersionProbe(first, first);
            }
            if (exit != 0)
            {
                var tail = TailLines(stderr, 5);
                return new VersionProbe(null,
                    $"exited {exit}" + (tail.Length > 0 ? $": {tail}" : " with no error output."));
            }
            return new VersionProbe(null, "exited 0 but printed nothing.");
        }
        catch (OperationCanceledException) when (!ct.IsCancellationRequested)
        {
            return new VersionProbe(null,
                $"did not respond within {timeout.TotalSeconds:0}s. Self-extracting builds are slow on their "
                + "first run; try again, and check that the server can write to its temp directory.");
        }
        catch (Exception ex)
        {
            // The interesting case: the OS refused to launch it at all (wrong
            // architecture, missing loader, quarantined, not executable).
            return new VersionProbe(null, $"could not be launched — {ex.GetType().Name}: {ex.Message}");
        }
    }

    /// <summary>Keeps the last <paramref name="maxLines"/> lines of a tool's stderr — enough to explain a failure without storing megabytes.</summary>
    public static string TailLines(string text, int maxLines = 20)
    {
        if (string.IsNullOrWhiteSpace(text))
        {
            return string.Empty;
        }
        var lines = text.Split('\n', StringSplitOptions.RemoveEmptyEntries)
            .Select(l => l.TrimEnd('\r'))
            .Where(l => l.Trim().Length > 0)
            .ToList();
        return lines.Count <= maxLines
            ? string.Join('\n', lines)
            : string.Join('\n', lines.Skip(lines.Count - maxLines));
    }
}
