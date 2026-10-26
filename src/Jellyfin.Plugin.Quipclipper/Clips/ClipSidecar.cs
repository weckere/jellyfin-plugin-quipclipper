using System;
using System.Globalization;
using System.IO;
using System.Text.Json;

namespace Jellyfin.Plugin.Quipclipper.Clips;

/// <summary>A span in seconds (<c>line</c>, <c>clip</c>).</summary>
/// <param name="Start">Where it starts.</param>
/// <param name="End">Where it ends, when known.</param>
public sealed record ClipSpan(double Start, double? End);

/// <summary>What Jellyfin knew about the clip's source when quipclipper cut it.</summary>
/// <param name="Id">The source item's id in this Jellyfin.</param>
/// <param name="Type"><c>Episode</c>, <c>Movie</c>, …</param>
/// <param name="Show">The series name for an episode, the title for a movie.</param>
/// <param name="Season">The season number.</param>
/// <param name="Episode">The episode number.</param>
/// <param name="Title">The episode's or movie's title.</param>
/// <param name="Year">The production year.</param>
public sealed record JellyfinSource(
    Guid? Id, string? Type, string? Show, int? Season, int? Episode, string? Title, int? Year);

/// <summary>Season, episode and year read from names (backfilled sidecars only).</summary>
/// <param name="Season">The season number.</param>
/// <param name="Episode">The episode number.</param>
/// <param name="Year">The year.</param>
public sealed record ParsedName(int? Season, int? Episode, int? Year);

/// <summary>
/// The hidden sidecar quipclipper writes beside every clip, <c>.&lt;clip file name&gt;.qc.json</c>
/// (schema <c>quipclipper.clip-meta</c> v1, quipclipper's docs/API.md "The metadata
/// sidecar"). Every field but <c>schema</c>, <c>version</c>, <c>file</c>, <c>kind</c> and
/// <c>created</c> is optional, and a backfilled sidecar may hold <c>null</c> anywhere, so
/// the parser reads each field on its own and ignores what it doesn't recognise.
/// </summary>
public sealed class ClipSidecar
{
    /// <summary>The schema name every sidecar carries.</summary>
    public const string Schema = "quipclipper.clip-meta";

    /// <summary>The sidecar's file-name suffix.</summary>
    public const string Suffix = ".qc.json";

    /// <summary>A sidecar is a few hundred bytes; anything over this isn't one of ours.</summary>
    public const long MaxBytes = 256 * 1024;

    /// <summary>Gets the schema version.</summary>
    public int Version { get; init; }

    /// <summary>Gets the clip's file name.</summary>
    public string? File { get; init; }

    /// <summary>Gets <c>audio</c>, <c>video</c>, <c>gif</c> or <c>set</c>.</summary>
    public string? Kind { get; init; }

    /// <summary>Gets how it was made (<c>clip</c>, <c>backfill</c>, …).</summary>
    public string? Origin { get; init; }

    /// <summary>Gets when the clip was cut.</summary>
    public DateTimeOffset? Created { get; init; }

    /// <summary>Gets the line's text.</summary>
    public string? Cue { get; init; }

    /// <summary>Gets a set's name (sets only).</summary>
    public string? Name { get; init; }

    /// <summary>Gets the source's display name as quipclipper names it.</summary>
    public string? SourceName { get; init; }

    /// <summary>Gets the line's span in the source.</summary>
    public ClipSpan? Line { get; init; }

    /// <summary>Gets the span cut from the source (the line plus padding).</summary>
    public ClipSpan? Clip { get; init; }

    /// <summary>Gets what Jellyfin knew about the source, or null.</summary>
    public JellyfinSource? Jellyfin { get; init; }

    /// <summary>Gets season, episode and year read from names (backfill only).</summary>
    public ParsedName? Parsed { get; init; }

    /// <summary>The sidecar's path for a clip: <c>.&lt;name&gt;.qc.json</c> in the same folder.</summary>
    /// <param name="clipPath">The clip's path.</param>
    /// <returns>The sidecar's path.</returns>
    public static string PathFor(string clipPath)
    {
        var dir = Path.GetDirectoryName(clipPath) ?? string.Empty;
        return Path.Combine(dir, "." + Path.GetFileName(clipPath) + Suffix);
    }

    /// <summary>Whether a file name is a sidecar's (<c>.x.qc.json</c>).</summary>
    /// <param name="fileName">A file name, without folder.</param>
    /// <returns>True for a sidecar's name.</returns>
    public static bool IsSidecarName(string fileName)
        => fileName.Length > Suffix.Length + 1
           && fileName[0] == '.'
           && fileName.EndsWith(Suffix, StringComparison.Ordinal);

    /// <summary>Reads a sidecar file. Never throws for a missing, unreadable or foreign file.</summary>
    /// <param name="sidecarPath">The sidecar's path.</param>
    /// <returns>The sidecar, or null.</returns>
    public static ClipSidecar? Read(string sidecarPath)
    {
        try
        {
            var info = new FileInfo(sidecarPath);
            if (!info.Exists || info.Length > MaxBytes)
            {
                return null;
            }

            return Parse(System.IO.File.ReadAllText(sidecarPath));
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or System.Security.SecurityException)
        {
            return null;
        }
    }

    /// <summary>Parses a sidecar's JSON. Never throws.</summary>
    /// <param name="json">The file's text.</param>
    /// <returns>The sidecar, or null when it isn't JSON or isn't quipclipper's.</returns>
    public static ClipSidecar? Parse(string json)
    {
        JsonDocument doc;
        try
        {
            doc = JsonDocument.Parse(json, new JsonDocumentOptions { MaxDepth = 32 });
        }
        catch (JsonException)
        {
            return null;
        }

        using (doc)
        {
            var root = doc.RootElement;
            if (root.ValueKind != JsonValueKind.Object || Str(root, "schema") != Schema)
            {
                return null;
            }

            var source = Obj(root, "source");
            return new ClipSidecar
            {
                Version = Int(root, "version") ?? 0,
                File = Str(root, "file"),
                Kind = Str(root, "kind"),
                Origin = Str(root, "origin"),
                Created = Date(root, "created"),
                Cue = Str(root, "cue"),
                Name = Str(root, "name"),
                SourceName = source is { } s ? Str(s, "name") : null,
                Line = Span(root, "line"),
                Clip = Span(root, "clip"),
                Jellyfin = JellyfinOf(Obj(root, "jellyfin")),
                Parsed = Obj(root, "parsed") is { } p ? new ParsedName(Int(p, "season"), Int(p, "episode"), Int(p, "year")) : null,
            };
        }
    }

    private static JellyfinSource? JellyfinOf(JsonElement? el)
    {
        if (el is not { } j)
        {
            return null;
        }

        Guid? id = Guid.TryParse(Str(j, "id"), out var g) && g != Guid.Empty ? g : null;
        var jf = new JellyfinSource(id, Str(j, "type"), Str(j, "show"), Int(j, "season"), Int(j, "episode"), Str(j, "title"), Int(j, "year"));
        return jf.Id is null && jf.Show is null && jf.Title is null ? null : jf;
    }

    private static JsonElement? Obj(JsonElement el, string name)
        => el.TryGetProperty(name, out var v) && v.ValueKind == JsonValueKind.Object ? v : null;

    private static string? Str(JsonElement el, string name)
    {
        if (!el.TryGetProperty(name, out var v) || v.ValueKind != JsonValueKind.String)
        {
            return null;
        }

        var s = v.GetString();
        return string.IsNullOrWhiteSpace(s) ? null : s;
    }

    private static double? Num(JsonElement el, string name)
        => el.TryGetProperty(name, out var v) && v.ValueKind == JsonValueKind.Number && v.TryGetDouble(out var d)
           && double.IsFinite(d) ? d : null;

    private static int? Int(JsonElement el, string name)
        => Num(el, name) is { } d && d >= int.MinValue && d <= int.MaxValue && d == Math.Floor(d) ? (int)d : null;

    private static ClipSpan? Span(JsonElement el, string name)
        => Obj(el, name) is { } o && Num(o, "start") is { } start && start >= 0 ? new ClipSpan(start, Num(o, "end")) : null;

    private static DateTimeOffset? Date(JsonElement el, string name)
        => DateTimeOffset.TryParse(
            Str(el, name),
            CultureInfo.InvariantCulture,
            DateTimeStyles.AssumeUniversal | DateTimeStyles.AdjustToUniversal,
            out var d) ? d : null;
}
