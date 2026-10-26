using MediaBrowser.Model.Plugins;

namespace Jellyfin.Plugin.Quipclipper.Configuration;

/// <summary>
/// The plugin's settings. Clip metadata needs none (it reads the sidecars quipclipper
/// writes beside each clip); pairing with a quipclipper server comes in a later phase.
/// </summary>
public class PluginConfiguration : BasePluginConfiguration
{
}
