using System;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using MediaBrowser.Controller.Entities;
using MediaBrowser.Controller.Providers;
using Microsoft.Extensions.Logging;

namespace Jellyfin.Plugin.Quipclipper.Clips;

/// <summary>
/// Names a folder of clips after the source they share ("Crank Yankers · S01E01 · …",
/// "Bambi (1942)") or the show they share, instead of the release's folder name. A folder of
/// mixed sources, or with no sidecars, keeps Jellyfin's defaults.
/// </summary>
public class ClipFolderMetadataProvider : ILocalMetadataProvider<Folder>, IHasItemChangeMonitor
{
    private readonly ILogger<ClipFolderMetadataProvider> _logger;
    private readonly Feature _feature;

    /// <summary>
    /// Initializes a new instance of the <see cref="ClipFolderMetadataProvider"/> class.
    /// </summary>
    /// <param name="logger">The logger.</param>
    public ClipFolderMetadataProvider(ILogger<ClipFolderMetadataProvider> logger)
        : this(logger, Feature.ClipMetadata)
    {
    }

    internal ClipFolderMetadataProvider(ILogger<ClipFolderMetadataProvider> logger, Feature feature)
    {
        _logger = logger;
        _feature = feature;
    }

    internal Feature Feature => _feature;

    /// <inheritdoc />
    public string Name => "quipclipper";

    /// <inheritdoc />
    public Task<MetadataResult<Folder>> GetMetadata(ItemInfo info, IDirectoryService directoryService, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(info);
        return Task.FromResult(_feature.Run(_logger, () => Read(info, directoryService), Nothing));
    }

    /// <inheritdoc />
    public bool HasChanged(BaseItem item, IDirectoryService directoryService)
    {
        ArgumentNullException.ThrowIfNull(item);
        return _feature.Run(_logger, () => Changed(item, directoryService), false);
    }

    // Jellyfin's defaults; built here, not in GetMetadata, so the guard covers it.
    private static MetadataResult<Folder> Nothing() => new();

    private MetadataResult<Folder> Read(ItemInfo info, IDirectoryService directoryService)
    {
        var result = new MetadataResult<Folder>();
        if (info.ItemType != typeof(Folder) || string.IsNullOrEmpty(info.Path))
        {
            return result;
        }

        try
        {
            if (FolderDetails.From(Sidecars.In(info.Path, directoryService)) is not { } details)
            {
                return result;
            }

            result.Item = new Folder
            {
                Name = details.Name,
                ProductionYear = details.ProductionYear,
                Tags = details.Tags.ToArray(),
            };
            result.HasMetadata = true;
        }
        catch (Exception ex) when (ex is not OperationCanceledException && !Feature.IsMismatch(ex))
        {
            Log.MetadataFailed(_logger, ex, info.Path);
            result = new MetadataResult<Folder>();
        }

        return result;
    }

    private bool Changed(BaseItem item, IDirectoryService directoryService)
    {
        try
        {
            return item.GetType() == typeof(Folder) && Sidecars.Handles(item)
                   && Sidecars.FilesIn(item.Path, directoryService).Any(f => Sidecars.NewerThan(f, item));
        }
        catch (Exception ex) when (ex is not OperationCanceledException && !Feature.IsMismatch(ex))
        {
            Log.ChangeCheckFailed(_logger, ex, item.Path);
            return false;
        }
    }
}
