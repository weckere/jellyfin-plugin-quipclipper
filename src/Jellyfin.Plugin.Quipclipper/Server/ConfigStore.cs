using System;
using Jellyfin.Plugin.Quipclipper.Configuration;

namespace Jellyfin.Plugin.Quipclipper.Server;

/// <summary>Where the plugin's settings live; the tests use one in memory.</summary>
public interface IConfigStore
{
    /// <summary>Raised after the settings change (saved here or from the settings page).</summary>
    event EventHandler? Changed;

    /// <summary>Gets the settings as they are now.</summary>
    PluginConfiguration Current { get; }

    /// <summary>Saves the settings.</summary>
    /// <param name="configuration">The settings.</param>
    void Save(PluginConfiguration configuration);
}

/// <summary>The plugin's own configuration file (Jellyfin's plugin XML).</summary>
public sealed class PluginConfigStore : IConfigStore
{
    private EventHandler? _changed;

    /// <inheritdoc />
    public event EventHandler? Changed
    {
        add
        {
            if (_changed is null && Plugin.Instance is { } plugin)
            {
                plugin.ConfigurationChanged += OnPluginChanged;
            }

            _changed += value;
        }

        remove
        {
            _changed -= value;
            if (_changed is null && Plugin.Instance is { } plugin)
            {
                plugin.ConfigurationChanged -= OnPluginChanged;
            }
        }
    }

    /// <inheritdoc />
    public PluginConfiguration Current => Plugin.Instance?.Configuration ?? new PluginConfiguration();

    /// <inheritdoc />
    public void Save(PluginConfiguration configuration) => Plugin.Instance?.UpdateConfiguration(configuration);

    private void OnPluginChanged(object? sender, MediaBrowser.Model.Plugins.BasePluginConfiguration e) => _changed?.Invoke(this, EventArgs.Empty);
}
