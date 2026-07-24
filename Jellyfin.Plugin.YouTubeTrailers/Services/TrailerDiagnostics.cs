using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Linq;
using System.Net;
using System.Net.Http;
using System.Threading;
using System.Threading.Tasks;
using Microsoft.Extensions.Logging;

namespace Jellyfin.Plugin.YouTubeTrailers.Services;

/// <summary>
/// Everything the admin needs to answer "why didn't this trailer play?".
///
/// Two halves:
///  * a bounded in-memory <b>failure log</b> the resolver writes to whenever a
///    build dies, capturing the phase, the exact command, and the tail of the
///    tool's stderr — the stuff that otherwise scrolls past in the server log
///    mixed with everything else Jellyfin is doing;
///  * a <b>diagnose</b> runner that walks the whole pipeline for one video ID
///    (binaries → metadata → format selection → CDN reachability → an actual
///    short ffmpeg fetch) and reports which step breaks. Each stage failure has
///    a different fix (update yt-dlp / add cookies / set a proxy / open egress),
///    so pinpointing the stage is the entire game.
/// </summary>
public sealed class TrailerDiagnostics
{
    private const int MaxFailures = 100;

    private readonly ToolRunner _tools;
    private readonly IHttpClientFactory _httpClientFactory;
    private readonly ILogger<TrailerDiagnostics> _logger;

    private readonly object _sync = new();
    private readonly LinkedList<TrailerFailure> _failures = new();

    public TrailerDiagnostics(
        ToolRunner tools, IHttpClientFactory httpClientFactory, ILogger<TrailerDiagnostics> logger)
    {
        _tools = tools;
        _httpClientFactory = httpClientFactory;
        _logger = logger;
    }

    // ── Failure log ────────────────────────────────────────────────────────

    public void RecordFailure(TrailerFailure failure)
    {
        lock (_sync)
        {
            _failures.AddFirst(failure);
            while (_failures.Count > MaxFailures)
            {
                _failures.RemoveLast();
            }
        }
        _logger.LogWarning(
            "[YouTubeTrailers] {Phase} failed for {VideoId} ({Label}): {Reason}",
            failure.Phase, failure.VideoId, failure.Label ?? "unlabelled", failure.Reason);
    }

    public IReadOnlyList<TrailerFailure> RecentFailures(int limit = MaxFailures)
    {
        lock (_sync)
        {
            return _failures.Take(Math.Clamp(limit, 1, MaxFailures)).ToList();
        }
    }

    /// <summary>Failures recorded within the given window — drives the "N failures in 24h" stat.</summary>
    public int FailureCountSince(DateTime sinceUtc)
    {
        lock (_sync)
        {
            return _failures.Count(f => f.WhenUtc >= sinceUtc);
        }
    }

    public int ClearFailures()
    {
        lock (_sync)
        {
            var n = _failures.Count;
            _failures.Clear();
            return n;
        }
    }

    // ── Diagnose ───────────────────────────────────────────────────────────

    /// <summary>
    /// Runs the full pipeline check for one video ID. Never throws: every step
    /// is captured as pass / warn / fail with its raw tool output attached, so
    /// the config page can render the whole trace even when something explodes
    /// halfway through.
    /// </summary>
    public async Task<DiagnoseReport> DiagnoseAsync(
        string videoId, IEnumerable<DiagStep>? leadingSteps, CancellationToken ct)
    {
        var steps = new List<DiagStep>();
        if (leadingSteps is not null)
        {
            steps.AddRange(leadingSteps);
        }

        var cfg = Plugin.Instance?.Configuration;
        steps.Add(new DiagStep(
            "Plugin configuration",
            cfg is null ? "fail" : cfg.Enabled ? "ok" : "warn",
            cfg is null
                ? "Plugin instance unavailable."
                : cfg.Enabled
                    ? $"Enabled. Format selector: {cfg.FormatSelector}. Proxy: {(string.IsNullOrWhiteSpace(cfg.Proxy) ? "none" : cfg.Proxy)}. Extra yt-dlp args: {(string.IsNullOrWhiteSpace(cfg.YtDlpArguments) ? "none" : cfg.YtDlpArguments)}."
                    : "The plugin is disabled — every trailer request returns 404 regardless of anything below.",
            null));

        // ── 1. Binaries ────────────────────────────────────────────────────
        var ytDlpPath = _tools.ResolveYtDlp();
        if (ytDlpPath is null)
        {
            steps.Add(new DiagStep("yt-dlp binary", "fail",
                "No usable yt-dlp. The managed binary hasn't been downloaded and no working path is configured. "
                + "Use \"Download / update yt-dlp now\", or set an absolute path to a system yt-dlp.", null));
            return Finish(videoId, steps);
        }
        // A failing version probe is NOT proof the binary is unusable — it may
        // simply be slow to self-extract. So this step warns rather than fails,
        // and the run continues to the stages that actually exercise the tool;
        // if extraction then works, the admin can see the probe was the only
        // thing at fault instead of chasing a phantom "broken binary".
        var ytDlpProbe = await ToolRunner
            .ProbeVersionAsync(ytDlpPath, ["--version"], TimeSpan.FromSeconds(30), ct).ConfigureAwait(false);
        steps.Add(new DiagStep("yt-dlp binary",
            ytDlpProbe.Ok ? "ok" : "warn",
            ytDlpProbe.Ok
                ? $"{ytDlpPath} — version {ytDlpProbe.Version}. YouTube extraction breaks periodically; if this build is more than a few weeks old, update it first."
                : $"{ytDlpPath} did not report a version: {ytDlpProbe.Detail} The stages below run the binary for real — if they pass, only the version check is affected.",
            null));

        var ffmpegPath = _tools.ResolveFfmpeg();
        var ffmpegProbe = ffmpegPath is null
            ? new ToolRunner.VersionProbe(null, "no ffmpeg path could be resolved.")
            : await ToolRunner.ProbeVersionAsync(
                ffmpegPath, ["-hide_banner", "-version"], TimeSpan.FromSeconds(20), ct).ConfigureAwait(false);
        steps.Add(new DiagStep("ffmpeg binary",
            ffmpegProbe.Ok ? "ok" : "fail",
            ffmpegProbe.Ok
                ? $"{ffmpegPath} — {ffmpegProbe.Version}"
                : $"No usable ffmpeg (configured='{cfg?.FfmpegPath}', Jellyfin encoder='{_tools.EncoderPath}'): {ffmpegProbe.Detail}",
            null));

        // ── 2. Video metadata ──────────────────────────────────────────────
        // Deliberately separate from format selection: a video that is private,
        // removed, age-gated, or region-locked fails here with a clear reason,
        // whereas a video that exists but has no matching format fails at the
        // next step. Conflating them is what makes "no URLs" so unhelpful.
        var metaPsi = _tools.YtDlpPsi(
            "--print", "%(title)j",
            "--print", "%(duration)j",
            "--print", "%(age_limit)j",
            "--print", "%(availability)j",
            "--print", "%(live_status)j",
            $"https://www.youtube.com/watch?v={videoId}");
        if (metaPsi is not null)
        {
            var (exit, stdout, stderr, timedOut) = await _tools
                .RunWithTimeoutAsync(metaPsi, TimeSpan.FromSeconds(30), ct).ConfigureAwait(false);
            var lines = stdout.Split('\n', StringSplitOptions.RemoveEmptyEntries)
                .Select(l => l.Trim()).Where(l => l.Length > 0).ToList();
            if (exit == 0 && lines.Count >= 1)
            {
                var title = ToolRunner.ParseJsonString(lines[0]);
                var duration = lines.Count > 1 ? ToolRunner.ParseJsonNumber(lines[1]) : null;
                var ageLimit = lines.Count > 2 ? ToolRunner.ParseJsonNumber(lines[2]) : null;
                var availability = lines.Count > 3 ? ToolRunner.ParseJsonString(lines[3]) : null;
                var liveStatus = lines.Count > 4 ? ToolRunner.ParseJsonString(lines[4]) : null;

                var warn = (ageLimit is > 0) || (availability is not null and not "public");
                steps.Add(new DiagStep("Video metadata", warn ? "warn" : "ok",
                    $"\"{title ?? "(no title)"}\" · {(duration is null ? "unknown length" : TimeSpan.FromSeconds(duration.Value).ToString(@"m\:ss"))}"
                    + $" · availability: {availability ?? "public"}"
                    + $" · age limit: {(ageLimit is null or 0 ? "none" : ageLimit.Value.ToString("0", System.Globalization.CultureInfo.InvariantCulture) + "+")}"
                    + (string.IsNullOrEmpty(liveStatus) || liveStatus == "not_live" ? "" : $" · live status: {liveStatus}")
                    + (ageLimit is > 0 ? " — age-gated videos need --cookies or --cookies-from-browser in Extra yt-dlp arguments." : ""),
                    null));
            }
            else
            {
                steps.Add(new DiagStep("Video metadata", "fail",
                    timedOut
                        ? "yt-dlp timed out fetching metadata — the server can't reach YouTube, or a configured proxy is unreachable."
                        : InterpretYtDlpError(stderr),
                    ToolRunner.TailLines(stderr)));
                return Finish(videoId, steps);
            }
        }

        // ── 3. Format selection (the production resolve, verbatim) ─────────
        var resolved = await _tools.ResolveVideoAsync(videoId, ct).ConfigureAwait(false);
        if (!resolved.Ok)
        {
            steps.Add(new DiagStep("Format selection", "fail",
                resolved.TimedOut
                    ? "yt-dlp timed out selecting a format."
                    : resolved.Urls.Length == 0
                        ? "No stream URLs matched the configured format selector. "
                          + "If metadata above succeeded, the video exists but has no format satisfying the selector — "
                          + "try relaxing it (e.g. append /best) or lowering the height cap."
                        : $"Expected 1 (muxed) or 2 (video+audio) URLs, got {resolved.Urls.Length}.",
                ToolRunner.TailLines(resolved.Stderr) + "\n\n$ " + resolved.Command));
            return Finish(videoId, steps);
        }
        steps.Add(new DiagStep("Format selection", "ok",
            $"{(resolved.Urls.Length == 2 ? "Adaptive (separate video + audio)" : "Muxed (single stream)")}"
            + $" · {resolved.Urls.Length} URL(s) resolved"
            + (resolved.DurationSeconds is null ? "" : $" · {TimeSpan.FromSeconds(resolved.DurationSeconds.Value):m\\:ss}"),
            "$ " + resolved.Command));

        // ── 4. CDN reachability ────────────────────────────────────────────
        // The single most common silent failure: yt-dlp resolves fine (it talks
        // to youtube.com) but ffmpeg then can't fetch from googlevideo.com — a
        // dead CDN edge, a firewall that only allows youtube.com, or a proxy
        // applied to yt-dlp but not to the fetch. This range request reproduces
        // exactly what ffmpeg does first.
        for (var i = 0; i < resolved.Urls.Length; i++)
        {
            var label = resolved.Urls.Length == 2 ? (i == 0 ? "video" : "audio") : "muxed";
            steps.Add(await ProbeUrlAsync(label, resolved.Urls[i], ct).ConfigureAwait(false));
        }

        // ── 5. Real ffmpeg fetch ───────────────────────────────────────────
        if (ffmpegPath is not null)
        {
            steps.Add(await FfmpegFetchTestAsync(ffmpegPath, resolved.Urls, ct).ConfigureAwait(false));
        }

        return Finish(videoId, steps);
    }

    /// <summary>
    /// HEAD-equivalent range probe against a resolved googlevideo URL, honoring
    /// the configured proxy so a proxy misconfiguration shows up here rather
    /// than as a mysterious ffmpeg stall 20 seconds into a build.
    /// </summary>
    private async Task<DiagStep> ProbeUrlAsync(string label, string url, CancellationToken ct)
    {
        var name = $"Stream reachability ({label})";
        var host = Uri.TryCreate(url, UriKind.Absolute, out var uri) ? uri.Host : "?";
        var proxy = Plugin.Instance?.Configuration.Proxy;

        HttpClient client;
        HttpClientHandler? owned = null;
        if (!string.IsNullOrWhiteSpace(proxy))
        {
            try
            {
                owned = new HttpClientHandler { Proxy = new WebProxy(proxy), UseProxy = true };
                client = new HttpClient(owned);
            }
            catch (Exception ex)
            {
                return new DiagStep(name, "fail",
                    $"Configured proxy '{proxy}' is not a usable proxy URL: {ex.Message}", null);
            }
        }
        else
        {
            client = _httpClientFactory.CreateClient();
        }

        try
        {
            client.Timeout = TimeSpan.FromSeconds(15);
            using var req = new HttpRequestMessage(HttpMethod.Get, url);
            // Two bytes is enough to prove the edge will serve us; a HEAD is
            // unreliable against googlevideo (often 405).
            req.Headers.Range = new System.Net.Http.Headers.RangeHeaderValue(0, 1);
            var sw = Stopwatch.StartNew();
            using var resp = await client
                .SendAsync(req, HttpCompletionOption.ResponseHeadersRead, ct).ConfigureAwait(false);
            sw.Stop();

            var status = (int)resp.StatusCode;
            var ok = status is 200 or 206;
            var length = resp.Content.Headers.ContentRange?.Length ?? resp.Content.Headers.ContentLength;
            return new DiagStep(name, ok ? "ok" : "fail",
                $"HTTP {status} from {host} in {sw.ElapsedMilliseconds} ms"
                + (length is not null ? $" · stream size {length.Value / (1024 * 1024)} MB" : "")
                + (ok
                    ? ""
                    : status == 403
                        ? " — 403 usually means the signed URL was rejected (stale yt-dlp signature code, or the URL is IP-bound to a different address than the one fetching)."
                        : " — the CDN edge refused the request; a proxy or a different network path is likely needed."),
                null);
        }
        catch (OperationCanceledException) when (!ct.IsCancellationRequested)
        {
            return new DiagStep(name, "fail",
                $"Timed out after 15s connecting to {host}. This is the classic dead-edge case: yt-dlp resolves "
                + "(it talks to youtube.com) but the media host is unreachable from this server. Check egress "
                + "firewall rules for *.googlevideo.com, or set a proxy.", null);
        }
        catch (Exception ex)
        {
            return new DiagStep(name, "fail", $"{ex.GetType().Name}: {ex.Message}", null);
        }
        finally
        {
            if (owned is not null)
            {
                client.Dispose();
                owned.Dispose();
            }
        }
    }

    /// <summary>
    /// Stream-copies the first few seconds to the null muxer using the same
    /// reconnect/proxy flags the real build uses. Passing here means the build
    /// path works end to end and any failure is elsewhere (timeouts, disk).
    /// </summary>
    private async Task<DiagStep> FfmpegFetchTestAsync(string ffmpeg, string[] urls, CancellationToken ct)
    {
        var cfg = Plugin.Instance?.Configuration;
        var psi = new ProcessStartInfo
        {
            FileName = ffmpeg,
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            UseShellExecute = false,
        };
        psi.ArgumentList.Add("-hide_banner");
        psi.ArgumentList.Add("-loglevel");
        psi.ArgumentList.Add("error");
        foreach (var url in urls)
        {
            psi.ArgumentList.Add("-reconnect");
            psi.ArgumentList.Add("1");
            psi.ArgumentList.Add("-reconnect_on_network_error");
            psi.ArgumentList.Add("1");
            psi.ArgumentList.Add("-reconnect_streamed");
            psi.ArgumentList.Add("1");
            psi.ArgumentList.Add("-reconnect_delay_max");
            psi.ArgumentList.Add("5");
            if (!string.IsNullOrWhiteSpace(cfg?.Proxy))
            {
                psi.ArgumentList.Add("-http_proxy");
                psi.ArgumentList.Add(cfg.Proxy);
            }
            psi.ArgumentList.Add("-i");
            psi.ArgumentList.Add(url);
        }
        psi.ArgumentList.Add("-t");
        psi.ArgumentList.Add("3");
        psi.ArgumentList.Add("-c");
        psi.ArgumentList.Add("copy");
        psi.ArgumentList.Add("-f");
        psi.ArgumentList.Add("null");
        psi.ArgumentList.Add("-");
        if (!string.IsNullOrWhiteSpace(cfg?.Proxy))
        {
            psi.Environment["http_proxy"] = cfg.Proxy;
            psi.Environment["https_proxy"] = cfg.Proxy;
            psi.Environment["HTTP_PROXY"] = cfg.Proxy;
            psi.Environment["HTTPS_PROXY"] = cfg.Proxy;
        }

        var sw = Stopwatch.StartNew();
        var (exit, _, stderr, timedOut) = await _tools
            .RunWithTimeoutAsync(psi, TimeSpan.FromSeconds(30), ct).ConfigureAwait(false);
        sw.Stop();

        if (timedOut)
        {
            return new DiagStep("ffmpeg fetch test", "fail",
                "ffmpeg couldn't read 3 seconds of the stream within 30s. With reconnect enabled it will retry a "
                + "dead edge forever rather than exit — this is what the no-segment watchdog kills during a real build.",
                ToolRunner.TailLines(stderr));
        }
        return new DiagStep("ffmpeg fetch test", exit == 0 ? "ok" : "fail",
            exit == 0
                ? $"Stream-copied 3s successfully in {sw.ElapsedMilliseconds} ms — the full build path works for this video."
                : $"ffmpeg exited {exit} after {sw.ElapsedMilliseconds} ms.",
            ToolRunner.TailLines(stderr));
    }

    private static DiagnoseReport Finish(string videoId, List<DiagStep> steps)
    {
        var failed = steps.FirstOrDefault(s => s.Status == "fail");
        var warned = steps.Count(s => s.Status == "warn");
        var verdict = failed is not null ? "fail" : warned > 0 ? "warn" : "ok";
        var summary = failed is not null
            ? $"Blocked at: {failed.Name}"
            : warned > 0
                ? $"Pipeline works, {warned} warning(s) worth reading"
                : "Every stage passed — this trailer should build and play.";
        return new DiagnoseReport(videoId, DateTime.UtcNow, verdict, summary, steps);
    }

    /// <summary>
    /// Turns yt-dlp's stderr into the actionable sentence behind it. These five
    /// messages cover the overwhelming majority of real-world failures, and
    /// each has a completely different fix.
    /// </summary>
    private static string InterpretYtDlpError(string stderr)
    {
        var s = stderr ?? string.Empty;
        if (s.Contains("Sign in to confirm", StringComparison.OrdinalIgnoreCase)
            || s.Contains("bot", StringComparison.OrdinalIgnoreCase) && s.Contains("confirm", StringComparison.OrdinalIgnoreCase))
        {
            return "YouTube is bot-checking this server's IP. Add --cookies-from-browser BROWSER or --cookies /path/cookies.txt "
                 + "to Extra yt-dlp arguments, or route through a residential proxy.";
        }
        if (s.Contains("age", StringComparison.OrdinalIgnoreCase) && s.Contains("confirm", StringComparison.OrdinalIgnoreCase))
        {
            return "Age-restricted video. Supply cookies for a signed-in account via --cookies / --cookies-from-browser.";
        }
        if (s.Contains("not available in your country", StringComparison.OrdinalIgnoreCase)
            || s.Contains("blocked it in your country", StringComparison.OrdinalIgnoreCase))
        {
            return "Geo-blocked from this server's location. Set the Proxy option (it proxies both yt-dlp and the ffmpeg fetch).";
        }
        if (s.Contains("Private video", StringComparison.OrdinalIgnoreCase)
            || s.Contains("Video unavailable", StringComparison.OrdinalIgnoreCase)
            || s.Contains("has been removed", StringComparison.OrdinalIgnoreCase))
        {
            return "The video is private, removed, or otherwise unavailable. The metadata provider's trailer link is dead — "
                 + "refresh the item's metadata to pick up a replacement.";
        }
        if (s.Contains("Unable to extract", StringComparison.OrdinalIgnoreCase)
            || s.Contains("nsig extraction failed", StringComparison.OrdinalIgnoreCase)
            || s.Contains("player response", StringComparison.OrdinalIgnoreCase))
        {
            return "yt-dlp couldn't parse YouTube's player — this is what a YouTube-side change looks like. "
                 + "Update yt-dlp (button at the top of this page); a fix usually lands within hours.";
        }
        var tail = ToolRunner.TailLines(s, 3);
        return tail.Length > 0 ? tail : "yt-dlp failed without producing an error message.";
    }

}

/// <summary>One recorded build failure, shown in the config page's failure log.</summary>
public sealed record TrailerFailure(
    string VideoId,
    string? Label,
    string Phase,
    DateTime WhenUtc,
    int? ExitCode,
    string Reason,
    string StderrTail,
    string Command,
    long ElapsedMs);

/// <summary>One stage of a diagnose run. Status is <c>ok</c> / <c>warn</c> / <c>fail</c>.</summary>
public sealed record DiagStep(string Name, string Status, string Detail, string? Raw);

public sealed record DiagnoseReport(
    string VideoId, DateTime RanUtc, string Verdict, string Summary, IReadOnlyList<DiagStep> Steps);
