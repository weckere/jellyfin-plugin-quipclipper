using System;
using MediaBrowser.Model.Plugins;

namespace Jellyfin.Plugin.Quipclipper.Configuration;

/// <summary>
/// The plugin's settings. Clip metadata needs none (it reads the sidecars quipclipper
/// writes beside each clip). The subtitle watcher needs a quipclipper server it has paired
/// with: its URL and the client token pairing handed out, kept here because Jellyfin has no
/// secret store (this file is readable only by Jellyfin and its admins).
/// </summary>
public class PluginConfiguration : BasePluginConfiguration
{
    /// <summary>Gets or sets quipclipper's URL as this server reaches it, e.g. <c>http://172.31.0.1:8090</c>.</summary>
    public string ServerUrl { get; set; } = string.Empty;

    /// <summary>Gets or sets the client token (<c>qcc_…</c>) quipclipper gave this server when it paired; empty when not connected.</summary>
    public string Token { get; set; } = string.Empty;

    /// <summary>Gets or sets the client id quipclipper knows this server by (shown under its Connected apps).</summary>
    public string ClientId { get; set; } = string.Empty;

    /// <summary>Gets or sets the scopes the token was granted.</summary>
    public string[] Scopes { get; set; } = [];

    /// <summary>Gets or sets a value indicating whether subtitle changes are sent to quipclipper.</summary>
    public bool WatchSubtitles { get; set; } = true;

    /// <summary>Gets or sets the ids of the libraries whose subtitles are watched; empty watches every library.</summary>
    public string[] LibraryIds { get; set; } = [];

    /// <summary>Gets or sets why sending stopped for good (a revoked token), shown on the settings page; empty when fine.</summary>
    public string LastError { get; set; } = string.Empty;

    /// <summary>Gets a value indicating whether this server has paired with a quipclipper server.</summary>
    public bool Connected => !string.IsNullOrEmpty(Token) && Uri.TryCreate(ServerUrl, UriKind.Absolute, out _);
}
