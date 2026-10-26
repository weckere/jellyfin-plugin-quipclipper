using System;
using Microsoft.Extensions.Logging;

namespace Jellyfin.Plugin.Quipclipper.Clips;

/// <summary>The plugin's log messages. Every failure is logged and swallowed, never thrown into a scan.</summary>
internal static partial class Log
{
    [LoggerMessage(Level = LogLevel.Warning, Message = "quipclipper: could not read the clip metadata for {Path}")]
    public static partial void MetadataFailed(ILogger logger, Exception exception, string? path);

    [LoggerMessage(Level = LogLevel.Debug, Message = "quipclipper: change check failed for {Path}")]
    public static partial void ChangeCheckFailed(ILogger logger, Exception exception, string? path);

    [LoggerMessage(Level = LogLevel.Warning, Message = "quipclipper: no source artwork for {Path}")]
    public static partial void ImageFailed(ILogger logger, Exception exception, string? path);
}
