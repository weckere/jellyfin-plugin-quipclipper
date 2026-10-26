using System;
using System.Collections.Generic;
using System.Globalization;
using System.Linq;
using System.Text.RegularExpressions;

namespace Jellyfin.Plugin.Quipclipper.Clips;

/// <summary>
/// What a clip shows in Jellyfin, worked out from its sidecar alone (no Jellyfin types,
/// so it is tested without a server).
/// </summary>
public sealed partial class ClipDetails
{
    private const string Sep = " · ";

    /// <summary>Gets the title: the line, else the set's name, else the file name made readable.</summary>
    public required string Name { get; init; }

    /// <summary>Gets where the clip is from and when, e.g. "Crank Yankers · S01E01 · … · at 0:46 · cut 2026-06-21".</summary>
    public string? Overview { get; init; }

    /// <summary>Gets the tags: the show (or movie) Jellyfin knows the source as.</summary>
    public IReadOnlyList<string> Tags { get; init; } = [];

    /// <summary>Gets the source's year.</summary>
    public int? ProductionYear { get; init; }

    /// <summary>Gets when the clip was cut, as its premiere date (so "newest first" sorts work).</summary>
    public DateTime? PremiereDate { get; init; }

    /// <summary>Gets the source item in Jellyfin whose artwork the clip borrows.</summary>
    public Guid? SourceId { get; init; }

    /// <summary>Maps a sidecar to what the clip shows.</summary>
    /// <param name="sidecar">The clip's sidecar.</param>
    /// <param name="fileStem">The clip's file name without extension (the fallback title).</param>
    /// <returns>The clip's details.</returns>
    public static ClipDetails From(ClipSidecar sidecar, string fileStem)
    {
        ArgumentNullException.ThrowIfNull(sidecar);
        var jf = sidecar.Jellyfin;
        var name = CleanLine(sidecar.Cue)
                   ?? (sidecar.Kind == "set" ? CleanLine(sidecar.Name) : null)
                   ?? Readable(fileStem);

        var parts = new List<string>();
        if (SourceLabel(jf) is { } label)
        {
            parts.Add(label);
        }
        else if (CleanLine(sidecar.SourceName) is { } src)
        {
            parts.Add(sidecar.Parsed is { Season: { } s, Episode: { } e } ? src + Sep + EpisodeCode(s, e) : src);
        }

        if ((sidecar.Line ?? sidecar.Clip) is { } span)
        {
            parts.Add("at " + Timestamp(span.Start));
        }

        if (sidecar.Created is { } created)
        {
            parts.Add("cut " + created.UtcDateTime.ToString("yyyy-MM-dd", CultureInfo.InvariantCulture));
        }

        var show = CleanLine(jf?.Show);
        return new ClipDetails
        {
            Name = name,
            Overview = parts.Count > 0 ? string.Join(Sep, parts) : null,
            Tags = show is null ? [] : [show],
            ProductionYear = jf?.Year ?? sidecar.Parsed?.Year,
            PremiereDate = sidecar.Created?.UtcDateTime,
            SourceId = jf?.Id,
        };
    }

    /// <summary>"Crank Yankers · S01E01 · Tenacious D &amp; Wanda Sykes" for an episode, "Bambi (1942)" for a movie.</summary>
    /// <param name="jf">The sidecar's Jellyfin block.</param>
    /// <returns>The label, or null when there is nothing to say.</returns>
    public static string? SourceLabel(JellyfinSource? jf)
    {
        if (jf is null)
        {
            return null;
        }

        var show = CleanLine(jf.Show);
        var title = CleanLine(jf.Title);
        if (string.Equals(jf.Type, "Episode", StringComparison.OrdinalIgnoreCase))
        {
            var bits = new List<string>();
            if (show is not null)
            {
                bits.Add(show);
            }

            if (jf.Season is { } s && jf.Episode is { } e)
            {
                bits.Add(EpisodeCode(s, e));
            }

            if (title is not null && !string.Equals(title, show, StringComparison.OrdinalIgnoreCase))
            {
                bits.Add(title);
            }

            return bits.Count > 0 ? string.Join(Sep, bits) : null;
        }

        var main = title ?? show;
        if (main is null)
        {
            return null;
        }

        return jf.Year is { } y ? string.Create(CultureInfo.InvariantCulture, $"{main} ({y})") : main;
    }

    /// <summary>"S01E01".</summary>
    /// <param name="season">The season.</param>
    /// <param name="episode">The episode.</param>
    /// <returns>The code.</returns>
    public static string EpisodeCode(int season, int episode)
        => string.Create(CultureInfo.InvariantCulture, $"S{season:00}E{episode:00}");

    /// <summary>Seconds as "0:46", "40:20" or "1:07:52".</summary>
    /// <param name="seconds">Seconds into the source.</param>
    /// <returns>The timestamp.</returns>
    public static string Timestamp(double seconds)
    {
        var t = TimeSpan.FromSeconds(Math.Max(0, Math.Floor(seconds)));
        return t.TotalHours >= 1
            ? string.Create(CultureInfo.InvariantCulture, $"{(int)t.TotalHours}:{t.Minutes:00}:{t.Seconds:00}")
            : string.Create(CultureInfo.InvariantCulture, $"{t.Minutes}:{t.Seconds:00}");
    }

    /// <summary>A subtitle line as a title: one line, single spaces, no leading dialogue dash.</summary>
    /// <param name="text">The text.</param>
    /// <returns>The cleaned text, or null when nothing is left.</returns>
    public static string? CleanLine(string? text)
    {
        if (string.IsNullOrWhiteSpace(text))
        {
            return null;
        }

        var s = Whitespace().Replace(text, " ").Trim();
        s = LeadingDash().Replace(s, string.Empty).Trim();
        return s.Length > 0 ? s : null;
    }

    /// <summary>A file stem as quipclipper's listing titles it: underscores read as spaces.</summary>
    /// <param name="fileStem">The stem.</param>
    /// <returns>The title.</returns>
    public static string Readable(string fileStem)
        => CleanLine(fileStem.Replace('_', ' ')) ?? fileStem;

    [GeneratedRegex(@"\s+")]
    private static partial Regex Whitespace();

    [GeneratedRegex(@"^[-‐‑‒–—]+\s*")]
    private static partial Regex LeadingDash();
}

/// <summary>
/// What a per-source folder of clips shows: when every clip in it that Jellyfin knows comes
/// from one source, the folder is named after it; when they share a show, after the show.
/// A folder of mixed sources keeps its own name.
/// </summary>
public sealed class FolderDetails
{
    /// <summary>Gets the folder's title.</summary>
    public required string Name { get; init; }

    /// <summary>Gets the tags (the show).</summary>
    public IReadOnlyList<string> Tags { get; init; } = [];

    /// <summary>Gets the year, for a single source.</summary>
    public int? ProductionYear { get; init; }

    /// <summary>Gets one source item whose artwork stands for the folder (for episodes, its series' poster).</summary>
    public required Guid SourceId { get; init; }

    /// <summary>Works out a folder's details from its clips' sidecars.</summary>
    /// <param name="sidecars">The sidecars of the clips directly in the folder.</param>
    /// <returns>The details, or null when the clips don't share a source or show.</returns>
    public static FolderDetails? From(IEnumerable<ClipSidecar> sidecars)
    {
        var known = sidecars.Select(s => s.Jellyfin).OfType<JellyfinSource>().Where(j => j.Id is not null).ToList();
        if (known.Count == 0)
        {
            return null;
        }

        var first = known[0];
        if (known.All(j => j.Id == first.Id))
        {
            var label = ClipDetails.SourceLabel(first);
            return label is null ? null : new FolderDetails
            {
                Name = label,
                Tags = ClipDetails.CleanLine(first.Show) is { } show ? [show] : [],
                ProductionYear = first.Year,
                SourceId = first.Id!.Value,
            };
        }

        var shows = known.Select(j => ClipDetails.CleanLine(j.Show)).Distinct(StringComparer.OrdinalIgnoreCase).ToList();
        if (shows.Count == 1 && shows[0] is { } common
            && known.All(j => string.Equals(j.Type, "Episode", StringComparison.OrdinalIgnoreCase)))
        {
            return new FolderDetails { Name = common, Tags = [common], SourceId = first.Id!.Value };
        }

        return null;
    }
}
