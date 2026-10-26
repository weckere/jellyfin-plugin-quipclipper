using System;
using Jellyfin.Plugin.Quipclipper.Configuration;
using MediaBrowser.Common.Configuration;
using MediaBrowser.Common.Plugins;
using MediaBrowser.Model.Serialization;

namespace Jellyfin.Plugin.Quipclipper;

/// <summary>
/// The quipclipper plugin. Phase 1 is clip metadata: Jellyfin finds the providers in
/// <see cref="Clips"/> by type scan, so nothing is registered here.
/// </summary>
public class Plugin : BasePlugin<PluginConfiguration>
{
    /// <summary>The plugin's id, as in meta.json and the repository manifest.</summary>
    public static readonly Guid PluginId = Guid.Parse("6b6ea665-479b-4209-9ab3-a5e2a42275df");

    /// <summary>
    /// Initializes a new instance of the <see cref="Plugin"/> class.
    /// </summary>
    /// <param name="applicationPaths">Jellyfin's paths.</param>
    /// <param name="xmlSerializer">Jellyfin's XML serializer.</param>
    public Plugin(IApplicationPaths applicationPaths, IXmlSerializer xmlSerializer)
        : base(applicationPaths, xmlSerializer)
    {
    }

    /// <inheritdoc />
    public override string Name => "quipclipper";

    /// <inheritdoc />
    public override Guid Id => PluginId;

    /// <inheritdoc />
    public override string Description =>
        "Shows quipclipper's clips by their line, with the source's artwork, overview and show tag.";
}
