using System;
using System.Collections.Generic;
using System.IO;
using System.Threading;
using System.Threading.Tasks;
using MediaBrowser.Controller.Entities;
using MediaBrowser.Controller.Entities.TV;
using MediaBrowser.Controller.Library;
using MediaBrowser.Controller.Providers;
using MediaBrowser.Model.Drawing;
using MediaBrowser.Model.Entities;
using MediaBrowser.Model.IO;
using Microsoft.Extensions.Logging;

namespace Jellyfin.Plugin.Quipclipper.Clips;

/// <summary>
/// Gives a clip its source's artwork: an episode's still, a movie's poster; and a folder of
/// clips its series' poster or movie's poster. The source is looked up by the id in the
/// clip's sidecar in this Jellyfin's own library: no network.
/// </summary>
/// <remarks>
/// The image is handed over as a stream, never as a path, so Jellyfin saves a copy into the
/// clip's own metadata folder. Both other ways would put the source's artwork at risk:
/// Jellyfin deletes the file behind a <see cref="DynamicImageResponse.Path"/> once it has
/// copied it (it is meant for temporary files), and an item pointing at another item's
/// image file deletes that file when the clip's image is deleted or replaced.
/// </remarks>
public class ClipImageProvider : IDynamicImageProvider, IHasOrder
{
    private readonly ILibraryManager _libraryManager;
    private readonly IFileSystem _fileSystem;
    private readonly ILogger<ClipImageProvider> _logger;

    /// <summary>
    /// Initializes a new instance of the <see cref="ClipImageProvider"/> class.
    /// </summary>
    /// <param name="libraryManager">The library manager.</param>
    /// <param name="fileSystem">The file system.</param>
    /// <param name="logger">The logger.</param>
    public ClipImageProvider(ILibraryManager libraryManager, IFileSystem fileSystem, ILogger<ClipImageProvider> logger)
    {
        _libraryManager = libraryManager;
        _fileSystem = fileSystem;
        _logger = logger;
    }

    /// <inheritdoc />
    public string Name => "quipclipper";

    /// <summary>Gets the order: before the embedded image extractor (99) and the screen grabber (100).</summary>
    public int Order => 0;

    /// <inheritdoc />
    public bool Supports(BaseItem item) => item is not null && Sidecars.Handles(item);

    /// <inheritdoc />
    public IEnumerable<ImageType> GetSupportedImages(BaseItem item) => [ImageType.Primary];

    /// <inheritdoc />
    public Task<DynamicImageResponse> GetImage(BaseItem item, ImageType type, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(item);
        var none = new DynamicImageResponse { HasImage = false };
        if (type != ImageType.Primary || !Sidecars.Handles(item))
        {
            return Task.FromResult(none);
        }

        try
        {
            var path = item.GetType() == typeof(Folder) ? FolderImagePath(item) : ClipImagePath(item);
            if (path is null || ImageFormatOf(path) is not { } format)
            {
                return Task.FromResult(none);
            }

            var stream = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.Read, 4096, useAsync: true);
            return Task.FromResult(new DynamicImageResponse { HasImage = true, Stream = stream, Format = format });
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            Log.ImageFailed(_logger, ex, item.Path);
            return Task.FromResult(none);
        }
    }

    /// <summary>The artwork for a clip from a source: its own primary image, else its series'.</summary>
    /// <param name="source">The source item.</param>
    /// <param name="lookup">Finds an item by id (the series of an episode).</param>
    /// <returns>Image paths to try, best first.</returns>
    internal static IEnumerable<ItemImageInfo?> ClipCandidates(BaseItem source, Func<Guid, BaseItem?> lookup)
    {
        yield return source.GetImageInfo(ImageType.Primary, 0);
        yield return source.GetImageInfo(ImageType.Thumb, 0);
        if (source is Episode episode && !episode.SeriesId.Equals(Guid.Empty) && lookup(episode.SeriesId) is { } series)
        {
            yield return series.GetImageInfo(ImageType.Thumb, 0);
            yield return series.GetImageInfo(ImageType.Backdrop, 0);
            yield return series.GetImageInfo(ImageType.Primary, 0);
        }
    }

    /// <summary>The artwork for a folder of clips: a series' poster, else the source's own.</summary>
    /// <param name="source">One source item of the folder's clips.</param>
    /// <param name="lookup">Finds an item by id (the series of an episode).</param>
    /// <returns>Image paths to try, best first.</returns>
    internal static IEnumerable<ItemImageInfo?> FolderCandidates(BaseItem source, Func<Guid, BaseItem?> lookup)
    {
        if (source is Episode episode && !episode.SeriesId.Equals(Guid.Empty) && lookup(episode.SeriesId) is { } series)
        {
            yield return series.GetImageInfo(ImageType.Primary, 0);
        }

        yield return source.GetImageInfo(ImageType.Primary, 0);
    }

    /// <summary>The image format for a file name, or null for one Jellyfin can't take as artwork.</summary>
    /// <param name="path">The image's path.</param>
    /// <returns>The format.</returns>
    internal static ImageFormat? ImageFormatOf(string path)
        => Path.GetExtension(path).ToUpperInvariant() switch
        {
            ".JPG" or ".JPEG" => ImageFormat.Jpg,
            ".PNG" => ImageFormat.Png,
            ".WEBP" => ImageFormat.Webp,
            ".GIF" => ImageFormat.Gif,
            ".BMP" => ImageFormat.Bmp,
            _ => null,
        };

    private string? ClipImagePath(BaseItem item)
    {
        var sidecar = Sidecars.ForClip(item.Path, new DirectoryService(_fileSystem));
        return sidecar?.Jellyfin?.Id is { } id && Lookup(id) is { } source
            ? FirstLocal(ClipCandidates(source, Lookup))
            : null;
    }

    private string? FolderImagePath(BaseItem item)
    {
        var details = FolderDetails.From(Sidecars.In(item.Path, new DirectoryService(_fileSystem)));
        return details is not null && Lookup(details.SourceId) is { } source
            ? FirstLocal(FolderCandidates(source, Lookup))
            : null;
    }

    private BaseItem? Lookup(Guid id) => _libraryManager.GetItemById(id);

    private static string? FirstLocal(IEnumerable<ItemImageInfo?> candidates)
    {
        foreach (var image in candidates)
        {
            if (image is { IsLocalFile: true } && !string.IsNullOrEmpty(image.Path) && File.Exists(image.Path))
            {
                return image.Path;
            }
        }

        return null;
    }
}
