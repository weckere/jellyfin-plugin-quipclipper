using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text.Json;

namespace Jellyfin.Plugin.Quipclipper.Subtitles;

/// <summary>
/// What the watcher last saw: each media file's subtitle files (only files that have some),
/// the library folders it has looked through once (the baseline, which sends nothing), and
/// the files still to send to quipclipper. Kept in the plugin's data folder, so a restart
/// neither sends everything again nor loses what was waiting.
/// </summary>
public sealed class SubtitleState
{
    /// <summary>The most files kept waiting for quipclipper; past it the oldest are dropped.</summary>
    public const int MaxPending = 10_000;

    private const int Version = 1;
    private static readonly JsonSerializerOptions Json = new() { WriteIndented = false };
    private readonly object _lock = new();
    private readonly Dictionary<string, IReadOnlyDictionary<string, long>> _media = new(StringComparer.Ordinal);
    private readonly HashSet<string> _seeded = new(StringComparer.Ordinal);
    private readonly LinkedList<string> _pending = new();
    private readonly HashSet<string> _pendingSet = new(StringComparer.Ordinal);
    private bool _dirty;

    /// <summary>Gets how many files are waiting to be sent.</summary>
    public int PendingCount
    {
        get
        {
            lock (_lock)
            {
                return _pending.Count;
            }
        }
    }

    /// <summary>Gets how many media files with subtitles the baseline holds.</summary>
    public int Known
    {
        get
        {
            lock (_lock)
            {
                return _media.Count;
            }
        }
    }

    /// <summary>Gets how many waiting files were dropped because too many waited.</summary>
    public int Dropped { get; private set; }

    /// <summary>Gets the library folders looked through once.</summary>
    public IReadOnlyCollection<string> Seeded
    {
        get
        {
            lock (_lock)
            {
                return _seeded.ToArray();
            }
        }
    }

    /// <summary>Whether a library folder has its baseline.</summary>
    /// <param name="location">The library folder.</param>
    /// <returns>True once it was looked through.</returns>
    public bool IsSeeded(string location)
    {
        lock (_lock)
        {
            return _seeded.Contains(location);
        }
    }

    /// <summary>Marks a library folder as looked through.</summary>
    /// <param name="location">The library folder.</param>
    public void MarkSeeded(string location)
    {
        lock (_lock)
        {
            _dirty |= _seeded.Add(location);
        }
    }

    /// <summary>Notes a media file's subtitle files without sending anything (the baseline).</summary>
    /// <param name="mediaPath">The media file.</param>
    /// <param name="subtitles">Its subtitle files.</param>
    public void Record(string mediaPath, IReadOnlyDictionary<string, long> subtitles)
    {
        lock (_lock)
        {
            Set(mediaPath, subtitles);
        }
    }

    /// <summary>Notes a media file's subtitle files; when they differ from what was seen, it waits to be sent.</summary>
    /// <param name="mediaPath">The media file.</param>
    /// <param name="subtitles">Its subtitle files now.</param>
    /// <returns>True when they changed (and the file now waits to be sent).</returns>
    public bool Observe(string mediaPath, IReadOnlyDictionary<string, long> subtitles)
    {
        lock (_lock)
        {
            _media.TryGetValue(mediaPath, out var before);
            if (!SubtitleFiles.Differ(before, subtitles))
            {
                return false;
            }

            Set(mediaPath, subtitles);
            AddPending(mediaPath);
            return true;
        }
    }

    /// <summary>The next files to send, oldest first.</summary>
    /// <param name="max">At most this many.</param>
    /// <returns>The files (still waiting until <see cref="Sent"/>).</returns>
    public IReadOnlyList<string> NextBatch(int max)
    {
        lock (_lock)
        {
            return _pending.Take(max).ToList();
        }
    }

    /// <summary>Drops files from the waiting list: sent, or refused for good.</summary>
    /// <param name="paths">The files.</param>
    public void Sent(IEnumerable<string> paths)
    {
        ArgumentNullException.ThrowIfNull(paths);
        lock (_lock)
        {
            foreach (var p in paths)
            {
                if (_pendingSet.Remove(p))
                {
                    _pending.Remove(p);
                    _dirty = true;
                }
            }
        }
    }

    /// <summary>Forgets every library folder's baseline and what waits (after a disconnect: the next connection starts over).</summary>
    public void Reset()
    {
        lock (_lock)
        {
            _media.Clear();
            _seeded.Clear();
            _pending.Clear();
            _pendingSet.Clear();
            _dirty = true;
        }
    }

    /// <summary>Reads the state from <paramref name="file"/>; a missing or damaged file is an empty state (a new baseline, nothing sent).</summary>
    /// <param name="file">The state file.</param>
    /// <returns>The state.</returns>
    public static SubtitleState Load(string file)
    {
        var state = new SubtitleState();
        Saved? saved;
        try
        {
            saved = JsonSerializer.Deserialize<Saved>(File.ReadAllText(file), Json);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or JsonException or NotSupportedException)
        {
            return state;
        }

        if (saved is null || saved.Version != Version)
        {
            return state;
        }

        foreach (var (path, subs) in saved.Media ?? [])
        {
            if (subs is { Count: > 0 })
            {
                state._media[path] = new SortedDictionary<string, long>(subs, StringComparer.Ordinal);
            }
        }

        state._seeded.UnionWith(saved.Seeded ?? []);
        foreach (var p in saved.Pending ?? [])
        {
            state.AddPending(p);
        }

        state._dirty = false;
        state.Dropped = 0;
        return state;
    }

    /// <summary>Writes the state to <paramref name="file"/> when it changed, through a temporary file so a reader never sees half of it.</summary>
    /// <param name="file">The state file.</param>
    /// <returns>True when it was written.</returns>
    /// <exception cref="IOException">It couldn't be written.</exception>
    public bool SaveIfChanged(string file)
    {
        string text;
        lock (_lock)
        {
            if (!_dirty)
            {
                return false;
            }

            text = JsonSerializer.Serialize(
                new Saved
                {
                    Version = Version,
                    Seeded = _seeded.Order(StringComparer.Ordinal).ToList(),
                    Media = _media.ToDictionary(kv => kv.Key, kv => kv.Value.ToDictionary(s => s.Key, s => s.Value, StringComparer.Ordinal), StringComparer.Ordinal),
                    Pending = _pending.ToList(),
                },
                Json);
            _dirty = false;
        }

        Directory.CreateDirectory(Path.GetDirectoryName(file)!);
        var tmp = file + ".tmp";
        File.WriteAllText(tmp, text);
        File.Move(tmp, file, overwrite: true);
        return true;
    }

    private void Set(string mediaPath, IReadOnlyDictionary<string, long> subtitles)
    {
        _media.TryGetValue(mediaPath, out var before);
        if (!SubtitleFiles.Differ(before, subtitles))
        {
            return;
        }

        if (subtitles.Count == 0)
        {
            _media.Remove(mediaPath);
        }
        else
        {
            _media[mediaPath] = new SortedDictionary<string, long>(subtitles.ToDictionary(kv => kv.Key, kv => kv.Value), StringComparer.Ordinal);
        }

        _dirty = true;
    }

    private void AddPending(string mediaPath)
    {
        if (!_pendingSet.Add(mediaPath))
        {
            return;
        }

        _pending.AddLast(mediaPath);
        while (_pending.Count > MaxPending)
        {
            _pendingSet.Remove(_pending.First!.Value);
            _pending.RemoveFirst();
            Dropped++;
        }

        _dirty = true;
    }

    private sealed class Saved
    {
        public int Version { get; set; }

        public List<string>? Seeded { get; set; }

        public Dictionary<string, Dictionary<string, long>>? Media { get; set; }

        public List<string>? Pending { get; set; }
    }
}
