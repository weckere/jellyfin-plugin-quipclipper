using System;
using System.Collections.Generic;
using System.IO;
using MediaBrowser.Controller.Entities;
using MediaBrowser.Controller.Providers;

namespace Jellyfin.Plugin.Quipclipper.Clips;

/// <summary>
/// Finding sidecars through Jellyfin's per-refresh <see cref="IDirectoryService"/> cache, and
/// which items they apply to.
/// </summary>
internal static class Sidecars
{
    /// <summary>A folder with more clips than this gets no folder title (it reads every sidecar).</summary>
    public const int MaxPerFolder = 500;

    /// <summary>
    /// Whether an item is one the plugin handles: exactly a <see cref="Video"/> or a
    /// <see cref="Folder"/> (what a Home Videos library holds), never a movie, episode,
    /// series, season or library root, which are subclasses with providers of their own.
    /// </summary>
    /// <param name="item">The item.</param>
    /// <returns>True for a plain video or folder on disk.</returns>
    public static bool Handles(BaseItem item)
    {
        if (item.GetType() != typeof(Video) && item.GetType() != typeof(Folder))
        {
            return false;
        }

        try
        {
            return !string.IsNullOrEmpty(item.Path) && item.IsFileProtocol;
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            // IsFileProtocol asks a server service; without one, it's not ours to touch.
            return false;
        }
    }

    /// <summary>The sidecar file's entry for a clip, when there is one.</summary>
    /// <param name="clipPath">The clip's path.</param>
    /// <param name="directoryService">Jellyfin's directory cache.</param>
    /// <returns>The file entry, or null.</returns>
    public static MediaBrowser.Model.IO.FileSystemMetadata? FileFor(string clipPath, IDirectoryService directoryService)
    {
        var file = directoryService.GetFile(ClipSidecar.PathFor(clipPath));
        return file is { Exists: true, IsDirectory: false } ? file : null;
    }

    /// <summary>A clip's sidecar, or null when it has none or it can't be read.</summary>
    /// <param name="clipPath">The clip's path.</param>
    /// <param name="directoryService">Jellyfin's directory cache.</param>
    /// <returns>The sidecar, or null.</returns>
    public static ClipSidecar? ForClip(string clipPath, IDirectoryService directoryService)
        => FileFor(clipPath, directoryService) is { } file ? ClipSidecar.Read(file.FullName) : null;

    /// <summary>The sidecar files directly in a folder.</summary>
    /// <param name="folderPath">The folder.</param>
    /// <param name="directoryService">Jellyfin's directory cache.</param>
    /// <returns>The sidecar files (empty when there are more than <see cref="MaxPerFolder"/>).</returns>
    public static List<MediaBrowser.Model.IO.FileSystemMetadata> FilesIn(string folderPath, IDirectoryService directoryService)
    {
        var found = new List<MediaBrowser.Model.IO.FileSystemMetadata>();
        foreach (var file in directoryService.GetFiles(folderPath))
        {
            if (ClipSidecar.IsSidecarName(file.Name) && ClipFileExists(file.FullName, directoryService))
            {
                found.Add(file);
                if (found.Count > MaxPerFolder)
                {
                    return [];
                }
            }
        }

        return found;
    }

    /// <summary>The sidecars directly in a folder whose clips still exist.</summary>
    /// <param name="folderPath">The folder.</param>
    /// <param name="directoryService">Jellyfin's directory cache.</param>
    /// <returns>The sidecars.</returns>
    public static List<ClipSidecar> In(string folderPath, IDirectoryService directoryService)
    {
        var sidecars = new List<ClipSidecar>();
        foreach (var file in FilesIn(folderPath, directoryService))
        {
            if (ClipSidecar.Read(file.FullName) is { } sidecar)
            {
                sidecars.Add(sidecar);
            }
        }

        return sidecars;
    }

    /// <summary>Whether a sidecar changed after Jellyfin last saved the item.</summary>
    /// <param name="file">The sidecar's entry.</param>
    /// <param name="item">The item.</param>
    /// <returns>True when the sidecar is newer.</returns>
    public static bool NewerThan(MediaBrowser.Model.IO.FileSystemMetadata file, BaseItem item)
        => file.LastWriteTimeUtc > item.DateLastSaved;

    // A sidecar left behind by a clip moved away over SMB (quipclipper's backfill prunes those).
    private static bool ClipFileExists(string sidecarPath, IDirectoryService directoryService)
    {
        var name = Path.GetFileName(sidecarPath);
        var clipName = name[1..^ClipSidecar.Suffix.Length];
        var dir = Path.GetDirectoryName(sidecarPath) ?? string.Empty;
        return directoryService.GetFile(Path.Combine(dir, clipName)) is { Exists: true };
    }
}
