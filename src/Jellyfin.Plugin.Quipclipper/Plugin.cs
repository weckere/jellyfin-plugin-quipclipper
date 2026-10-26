using System;
using System.Collections.Generic;
using Jellyfin.Plugin.Quipclipper.Configuration;
using MediaBrowser.Common.Configuration;
using MediaBrowser.Common.Plugins;
using MediaBrowser.Model.Plugins;
using MediaBrowser.Model.Serialization;
using Microsoft.Extensions.Logging;

namespace Jellyfin.Plugin.Quipclipper;

/// <summary>
/// The quipclipper plugin. Clip metadata (Phase 1): Jellyfin finds the providers in
/// <see cref="Clips"/> by type scan. Subtitle re-index (Phase 2): the watcher and pairing,
/// registered by <see cref="PluginServiceRegistrator"/>, set up on the settings page.
/// </summary>
public class Plugin : BasePlugin<PluginConfiguration>, IHasWebPages
{
    /// <summary>The plugin's id, as in meta.json and the repository manifest.</summary>
    public static readonly Guid PluginId = Guid.Parse("6b6ea665-479b-4209-9ab3-a5e2a42275df");

    /// <summary>
    /// Initializes a new instance of the <see cref="Plugin"/> class.
    /// </summary>
    /// <param name="applicationPaths">Jellyfin's paths.</param>
    /// <param name="xmlSerializer">Jellyfin's XML serializer.</param>
    /// <param name="logger">The logger: says which features went off while the plugin's services were registered.</param>
    public Plugin(IApplicationPaths applicationPaths, IXmlSerializer xmlSerializer, ILogger<Plugin> logger)
        : base(applicationPaths, xmlSerializer)
    {
        Instance = this;
        Feature.LogUnlogged(logger);
    }

    /// <summary>Gets the running plugin (set when Jellyfin loads it), for its configuration.</summary>
    public static Plugin? Instance { get; private set; }

    /// <inheritdoc />
    public override string Name => "quipclipper";

    /// <inheritdoc />
    public override Guid Id => PluginId;

    /// <inheritdoc />
    public override string Description =>
        "Shows quipclipper's clips by their line, with the source's artwork, overview and show tag; " +
        "tells quipclipper when Jellyfin brings in a subtitle, so it is searchable at once.";

    /// <inheritdoc />
    public IEnumerable<PluginPageInfo> GetPages() =>
    [
        new PluginPageInfo
        {
            Name = Name,
            EmbeddedResourcePath = GetType().Namespace + ".Configuration.configPage.html",
        },
    ];
}
