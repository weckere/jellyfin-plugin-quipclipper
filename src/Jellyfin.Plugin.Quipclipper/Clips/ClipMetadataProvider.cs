using System;
using System.IO;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using MediaBrowser.Controller.Entities;
using MediaBrowser.Controller.Providers;
using Microsoft.Extensions.Logging;

namespace Jellyfin.Plugin.Quipclipper.Clips;

/// <summary>
/// Names a clip by its line, with an overview of where it's from, the show as a tag, the
/// source's year and the day it was cut, read from quipclipper's sidecar beside it. A local
/// provider: Jellyfin always runs it, with no library setting, and it needs no network.
/// A clip without a readable sidecar is left to Jellyfin's defaults.
/// </summary>
public class ClipMetadataProvider : ILocalMetadataProvider<Video>, IHasItemChangeMonitor
{
    private readonly ILogger<ClipMetadataProvider> _logger;

    /// <summary>
    /// Initializes a new instance of the <see cref="ClipMetadataProvider"/> class.
    /// </summary>
    /// <param name="logger">The logger.</param>
    public ClipMetadataProvider(ILogger<ClipMetadataProvider> logger)
    {
        _logger = logger;
    }

    /// <inheritdoc />
    public string Name => "quipclipper";

    /// <inheritdoc />
    public Task<MetadataResult<Video>> GetMetadata(ItemInfo info, IDirectoryService directoryService, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(info);
        var result = new MetadataResult<Video>();
        if (info.ItemType != typeof(Video) || string.IsNullOrEmpty(info.Path))
        {
            return Task.FromResult(result);
        }

        try
        {
            if (Sidecars.ForClip(info.Path, directoryService) is not { } sidecar)
            {
                return Task.FromResult(result);
            }

            var details = ClipDetails.From(sidecar, Path.GetFileNameWithoutExtension(info.Path));
            result.Item = new Video
            {
                Name = details.Name,
                Overview = details.Overview,
                ProductionYear = details.ProductionYear,
                PremiereDate = details.PremiereDate,
                Tags = details.Tags.ToArray(),
            };
            result.HasMetadata = true;
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            // Never fail a library scan over one clip: fall back to Jellyfin's defaults.
            Log.MetadataFailed(_logger, ex, info.Path);
            result = new MetadataResult<Video>();
        }

        return Task.FromResult(result);
    }

    /// <inheritdoc />
    public bool HasChanged(BaseItem item, IDirectoryService directoryService)
    {
        ArgumentNullException.ThrowIfNull(item);
        try
        {
            return item.GetType() == typeof(Video) && Sidecars.Handles(item)
                   && Sidecars.FileFor(item.Path, directoryService) is { } file
                   && Sidecars.NewerThan(file, item);
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            Log.ChangeCheckFailed(_logger, ex, item.Path);
            return false;
        }
    }
}
