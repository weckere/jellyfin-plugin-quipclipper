using System;
using Microsoft.Extensions.Logging;

namespace Jellyfin.Plugin.Quipclipper.Subtitles;

/// <summary>The subtitle watcher's log messages. Every failure is logged and swallowed, never thrown into Jellyfin.</summary>
internal static partial class Log
{
    [LoggerMessage(Level = LogLevel.Information, Message = "quipclipper: looked through {Location} for its subtitles ({Files} media files have some); changes from now on are sent")]
    public static partial void Seeded(ILogger logger, string location, int files);

    [LoggerMessage(Level = LogLevel.Information, Message = "quipclipper: sent {Sent} files with changed subtitles to be re-indexed ({Refused} refused)")]
    public static partial void Sent(ILogger logger, int sent, int refused);

    [LoggerMessage(Level = LogLevel.Warning, Message = "quipclipper: couldn't send changed subtitles ({Detail}); {Pending} files wait, trying again in {Pause}")]
    public static partial void SendFailed(ILogger logger, string? detail, int pending, TimeSpan pause);

    [LoggerMessage(Level = LogLevel.Warning, Message = "quipclipper: stopped sending changed subtitles: {Detail}. Connect again on the plugin's page")]
    public static partial void TokenRefused(ILogger logger, string? detail);

    [LoggerMessage(Level = LogLevel.Warning, Message = "quipclipper: couldn't save the subtitle watcher's state to {File}")]
    public static partial void StateNotSaved(ILogger logger, Exception exception, string file);

    [LoggerMessage(Level = LogLevel.Warning, Message = "quipclipper: the subtitle watcher failed; it carries on")]
    public static partial void WatchFailed(ILogger logger, Exception exception);

    [LoggerMessage(Level = LogLevel.Debug, Message = "quipclipper: couldn't note an item change")]
    public static partial void NotifyFailed(ILogger logger, Exception exception);
}
