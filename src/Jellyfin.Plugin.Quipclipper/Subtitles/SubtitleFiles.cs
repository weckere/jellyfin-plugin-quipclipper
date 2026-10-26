using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;

namespace Jellyfin.Plugin.Quipclipper.Subtitles;

/// <summary>
/// The subtitle files beside a media file, as quipclipper finds them: in the same folder,
/// named like the media file up to its extension (<c>Show.S01E01.mkv</c> owns
/// <c>Show.S01E01.en.srt</c>), the longest such name when two media files could own one.
/// Each with its last write time, so a rewritten file counts as a change too.
/// </summary>
public static class SubtitleFiles
{
    /// <summary>The subtitle formats quipclipper reads.</summary>
    public static readonly IReadOnlySet<string> Extensions = new HashSet<string>(
        [".srt", ".vtt", ".ass", ".ssa", ".sub"], StringComparer.OrdinalIgnoreCase);

    /// <summary>The media files quipclipper indexes (its VIDEO_EXTS and AUDIO_EXTS).</summary>
    public static readonly IReadOnlySet<string> MediaExtensions = new HashSet<string>(
        [".mkv", ".mp4", ".m4v", ".mov", ".avi", ".webm", ".ts",
         ".mp3", ".m4a", ".m4b", ".aac", ".flac", ".opus", ".ogg", ".oga", ".wav", ".aiff", ".aif", ".wma"],
        StringComparer.OrdinalIgnoreCase);

    /// <summary>Whether a path is a media file quipclipper could index.</summary>
    /// <param name="path">The path.</param>
    /// <returns>True for a media extension.</returns>
    public static bool IsMedia(string? path) => !string.IsNullOrEmpty(path) && MediaExtensions.Contains(Path.GetExtension(path));

    /// <summary>The subtitle files of one media file: file name → last write time (UTC ticks). Empty when none, or the folder can't be read.</summary>
    /// <param name="mediaPath">The media file's path.</param>
    /// <returns>Its subtitle files.</returns>
    public static IReadOnlyDictionary<string, long> For(string mediaPath)
    {
        var dir = Path.GetDirectoryName(mediaPath);
        if (string.IsNullOrEmpty(dir))
        {
            return Empty;
        }

        return InFolder(dir).TryGetValue(mediaPath, out var found) ? found : Empty;
    }

    /// <summary>Every media file in a folder that has subtitle files, with them; one listing of the folder.</summary>
    /// <param name="dir">The folder.</param>
    /// <returns>Media path → its subtitle files. Empty when the folder can't be read.</returns>
    public static Dictionary<string, IReadOnlyDictionary<string, long>> InFolder(string dir)
    {
        List<FileInfo> files;
        try
        {
            files = new DirectoryInfo(dir).EnumerateFiles().ToList();
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or System.Security.SecurityException)
        {
            return [];
        }

        var media = files.Where(f => MediaExtensions.Contains(f.Extension)).ToList();
        var owned = new Dictionary<string, SortedDictionary<string, long>>(StringComparer.Ordinal);
        foreach (var sub in files.Where(f => Extensions.Contains(f.Extension)))
        {
            FileInfo? owner = null;
            foreach (var m in media)
            {
                var stem = Path.GetFileNameWithoutExtension(m.Name);
                if (sub.Name.StartsWith(stem + ".", StringComparison.OrdinalIgnoreCase)
                    && (owner is null || stem.Length > Path.GetFileNameWithoutExtension(owner.Name).Length))
                {
                    owner = m;
                }
            }

            if (owner is null)
            {
                continue;
            }

            long ticks;
            try
            {
                ticks = sub.LastWriteTimeUtc.Ticks;
            }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
            {
                continue; // gone since the listing
            }

            var key = Path.Combine(dir, owner.Name);
            if (!owned.TryGetValue(key, out var subs))
            {
                owned[key] = subs = new SortedDictionary<string, long>(StringComparer.Ordinal);
            }

            subs[sub.Name] = ticks;
        }

        return owned.ToDictionary(kv => kv.Key, kv => (IReadOnlyDictionary<string, long>)kv.Value, StringComparer.Ordinal);
    }

    /// <summary>Every folder under <paramref name="root"/>, itself first; hidden folders and ones that can't be read are skipped.</summary>
    /// <param name="root">The library folder.</param>
    /// <returns>The folders.</returns>
    public static IEnumerable<string> Folders(string root)
    {
        var stack = new Stack<string>();
        stack.Push(root);
        while (stack.Count > 0)
        {
            var dir = stack.Pop();
            yield return dir;
            string[] subdirs;
            try
            {
                subdirs = Directory.GetDirectories(dir);
            }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or System.Security.SecurityException)
            {
                continue;
            }

            foreach (var sub in subdirs.OrderByDescending(s => s, StringComparer.Ordinal))
            {
                // Hidden folders, and links to folders (a link cycle would walk forever).
                if (!Path.GetFileName(sub).StartsWith('.') && !IsLink(sub))
                {
                    stack.Push(sub);
                }
            }
        }
    }

    private static bool IsLink(string dir)
    {
        try
        {
            return new DirectoryInfo(dir).LinkTarget is not null;
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            return true;
        }
    }

    /// <summary>Whether two sets of subtitle files differ: one added, removed or rewritten.</summary>
    /// <param name="a">One set.</param>
    /// <param name="b">The other.</param>
    /// <returns>True when they differ.</returns>
    public static bool Differ(IReadOnlyDictionary<string, long>? a, IReadOnlyDictionary<string, long>? b)
    {
        a ??= Empty;
        b ??= Empty;
        return a.Count != b.Count || a.Any(kv => !b.TryGetValue(kv.Key, out var t) || t != kv.Value);
    }

    private static readonly IReadOnlyDictionary<string, long> Empty = new Dictionary<string, long>();
}
