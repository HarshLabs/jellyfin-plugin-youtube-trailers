using System.Text.RegularExpressions;

namespace Jellyfin.Plugin.YouTubeTrailers.Services;

/// <summary>
/// Pulls YouTube video IDs out of the trailer URLs Jellyfin stores on library
/// items (<c>BaseItem.RemoteTrailers</c>, populated by the TMDb/TVDb metadata
/// providers). Those are almost always <c>youtube.com/watch?v=…</c> but the
/// providers also emit <c>youtu.be</c> short links and, occasionally, embed
/// URLs — so accept every shape rather than silently skipping items.
/// </summary>
public static class YouTubeLink
{
    // A YouTube ID is exactly 11 chars of [A-Za-z0-9_-]. Same pattern the
    // resolver validates with; kept here so callers can pre-filter without
    // reaching into TrailerResolver.
    private static readonly Regex IdPattern = new("^[A-Za-z0-9_-]{11}$", RegexOptions.Compiled);

    // watch?v= / youtu.be/ / embed/ / shorts/ / v/ / live/ — the ID is always
    // the 11-char token right after the marker. Anchoring on the marker (not
    // just "any 11-char run") avoids matching random path segments.
    private static readonly Regex UrlPattern = new(
        @"(?:youtube(?:-nocookie)?\.com/(?:watch\?(?:[^#]*&)?v=|embed/|shorts/|v/|live/)|youtu\.be/)([A-Za-z0-9_-]{11})",
        RegexOptions.Compiled | RegexOptions.IgnoreCase);

    public static bool IsValidId(string? id) => !string.IsNullOrEmpty(id) && IdPattern.IsMatch(id);

    /// <summary>
    /// Extracts the video ID from a trailer URL, or returns the input unchanged
    /// when it already *is* a bare ID (the admin config page lets you paste
    /// either). Returns null when nothing YouTube-shaped is present — e.g. a
    /// Vimeo trailer, which this plugin can't serve.
    /// </summary>
    public static string? ExtractId(string? urlOrId)
    {
        if (string.IsNullOrWhiteSpace(urlOrId))
        {
            return null;
        }
        var trimmed = urlOrId.Trim();
        if (IdPattern.IsMatch(trimmed))
        {
            return trimmed;
        }
        var match = UrlPattern.Match(trimmed);
        return match.Success ? match.Groups[1].Value : null;
    }
}
