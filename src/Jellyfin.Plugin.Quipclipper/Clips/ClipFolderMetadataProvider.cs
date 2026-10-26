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

    /// <summary>
    /// Initializes a new instance of the <see cref="ClipFolderMetadataProvider"/> class.
    /// </summary>
    /// <param name="logger">The logger.</param>
    public ClipFolderMetadataProvider(ILogger<ClipFolderMetadataProvider> logger)
    {
        _logger = logger;
    }

    /// <inheritdoc />
    public string Name => "quipclipper";

    /// <inheritdoc />
    public Task<MetadataResult<Folder>> GetMetadata(ItemInfo info, IDirectoryService directoryService, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(info);
        var result = new MetadataResult<Folder>();
        if (info.ItemType != typeof(Folder) || string.IsNullOrEmpty(info.Path))
        {
            return Task.FromResult(result);
        }

        try
        {
            if (FolderDetails.From(Sidecars.In(info.Path, directoryService)) is not { } details)
            {
                return Task.FromResult(result);
            }

            result.Item = new Folder
            {
                Name = details.Name,
                ProductionYear = details.ProductionYear,
                Tags = details.Tags.ToArray(),
            };
            result.HasMetadata = true;
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            Log.MetadataFailed(_logger, ex, info.Path);
            result = new MetadataResult<Folder>();
        }

        return Task.FromResult(result);
    }

    /// <inheritdoc />
    public bool HasChanged(BaseItem item, IDirectoryService directoryService)
    {
        ArgumentNullException.ThrowIfNull(item);
        try
        {
            return item.GetType() == typeof(Folder) && Sidecars.Handles(item)
                   && Sidecars.FilesIn(item.Path, directoryService).Any(f => Sidecars.NewerThan(f, item));
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            Log.ChangeCheckFailed(_logger, ex, item.Path);
            return false;
        }
    }
}
