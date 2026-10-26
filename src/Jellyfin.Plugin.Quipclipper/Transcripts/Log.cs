using System;
using Microsoft.Extensions.Logging;

namespace Jellyfin.Plugin.Quipclipper.Transcripts;

/// <summary>The transcript provider's log messages. A failed search is logged and answers nothing; it never throws into Jellyfin.</summary>
internal static partial class Log
{
    [LoggerMessage(Level = LogLevel.Warning, Message = "quipclipper: couldn't look up a machine transcript for {Path}: {Detail}")]
    public static partial void SearchFailed(ILogger logger, string path, string detail);

    [LoggerMessage(Level = LogLevel.Warning, Message = "quipclipper: the machine transcript search failed for {Path}")]
    public static partial void SearchCrashed(ILogger logger, Exception exception, string path);

    [LoggerMessage(Level = LogLevel.Information, Message = "quipclipper: handing Jellyfin the machine transcript ({Lines}) of {Path}")]
    public static partial void Fetched(ILogger logger, string lines, string path);
}
