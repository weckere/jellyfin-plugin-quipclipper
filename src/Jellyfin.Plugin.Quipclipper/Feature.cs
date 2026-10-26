using System;
using System.Collections.Generic;
using System.IO;
using System.Threading;
using System.Threading.Tasks;
using Microsoft.Extensions.Logging;

namespace Jellyfin.Plugin.Quipclipper;

/// <summary>
/// One of the plugin's features, which can go off on its own. A newer Jellyfin loads a
/// plugin built against an older one regardless of what changed, so a method the plugin
/// calls may be gone or a type changed shape. .NET reports that only when the code that
/// uses it first runs (<see cref="MissingMethodException"/>, <see cref="TypeLoadException"/>
/// and the like). Every entry point Jellyfin calls runs its work through
/// <see cref="Run{T}(ILogger, Func{T}, T)"/>: such a mismatch is logged once and turns that
/// one feature off until Jellyfin restarts, instead of failing every item of a scan. Any
/// other error is the entry point's own to handle (one item's bad sidecar, a network error).
/// </summary>
internal sealed class Feature
{
    /// <summary>Clips named by their line (the clip and folder metadata providers).</summary>
    public static readonly Feature ClipMetadata = new("clip metadata");

    /// <summary>Clips given their source's artwork (the image provider).</summary>
    public static readonly Feature ClipArtwork = new("clip artwork");

    /// <summary>Changed subtitles sent to quipclipper (the item events and the watcher's loop).</summary>
    public static readonly Feature SubtitleReindex = new("subtitle re-index");

    /// <summary>Machine transcripts in Jellyfin's subtitle search (the subtitle provider).</summary>
    public static readonly Feature Transcripts = new("machine transcripts");

    private int _off;
    private int _logged;

    /// <summary>Initializes a new instance of the <see cref="Feature"/> class (the tests make their own).</summary>
    /// <param name="name">What the log and the settings page call it.</param>
    public Feature(string name)
    {
        Name = name;
    }

    /// <summary>Gets every feature, as the settings page lists them.</summary>
    public static IReadOnlyList<Feature> All { get; } = [ClipMetadata, ClipArtwork, SubtitleReindex, Transcripts];

    /// <summary>Gets what the log and the settings page call the feature.</summary>
    public string Name { get; }

    /// <summary>Gets a value indicating whether the feature is on.</summary>
    public bool IsOn => Volatile.Read(ref _off) == 0;

    /// <summary>Gets why the feature went off; null while it is on.</summary>
    public string? Reason { get; private set; }

    /// <summary>Whether an exception says this Jellyfin isn't the one the plugin was built against.</summary>
    /// <param name="exception">The exception.</param>
    /// <returns>True for a missing member or type, or an assembly that won't load.</returns>
    public static bool IsMismatch(Exception? exception) => exception switch
    {
        MissingMemberException or TypeLoadException or BadImageFormatException or FileLoadException => true,
        // An assembly gone: its display name ("MediaBrowser.Controller, Version=…"), not a file path.
        FileNotFoundException { FileName: { } name } => name.Contains(", Version=", StringComparison.Ordinal),
        TypeInitializationException t => IsMismatch(t.InnerException),
        _ => false,
    };

    /// <summary>Runs an entry point's work, unless the feature is off.</summary>
    /// <typeparam name="T">What the work returns.</typeparam>
    /// <param name="logger">Where a mismatch is logged.</param>
    /// <param name="work">The work. Kept in its own method by the caller, so that a mismatch in it surfaces here.</param>
    /// <param name="off">What the entry point answers while the feature is off (Jellyfin's default).</param>
    /// <returns>What the work returned, or <paramref name="off"/>.</returns>
    public T Run<T>(ILogger logger, Func<T> work, T off)
    {
        ArgumentNullException.ThrowIfNull(work);
        if (!IsOn)
        {
            return off;
        }

        try
        {
            return work();
        }
        catch (Exception ex) when (IsMismatch(ex))
        {
            TurnOff(logger, ex);
            return off;
        }
    }

    /// <summary>Runs work that returns nothing, unless the feature is off.</summary>
    /// <param name="logger">Where a mismatch is logged.</param>
    /// <param name="work">The work.</param>
    public void Run(ILogger logger, Action work)
    {
        ArgumentNullException.ThrowIfNull(work);
        Run(
            logger,
            () =>
            {
                work();
                return true;
            },
            false);
    }

    /// <summary>Runs asynchronous work, unless the feature is off.</summary>
    /// <typeparam name="T">What the work returns.</typeparam>
    /// <param name="logger">Where a mismatch is logged.</param>
    /// <param name="work">The work.</param>
    /// <param name="off">What the entry point answers while the feature is off.</param>
    /// <returns>What the work returned, or <paramref name="off"/>.</returns>
    public async Task<T> RunAsync<T>(ILogger logger, Func<Task<T>> work, T off)
    {
        ArgumentNullException.ThrowIfNull(work);
        if (!IsOn)
        {
            return off;
        }

        try
        {
            return await work().ConfigureAwait(false);
        }
        catch (Exception ex) when (IsMismatch(ex))
        {
            TurnOff(logger, ex);
            return off;
        }
    }

    /// <summary>Turns the feature off for good (until Jellyfin restarts), logging why the first time.</summary>
    /// <param name="logger">Where to log it; null before Jellyfin has a logger to give (service registration), see <see cref="LogUnlogged"/>.</param>
    /// <param name="exception">The mismatch.</param>
    public void TurnOff(ILogger? logger, Exception exception)
    {
        ArgumentNullException.ThrowIfNull(exception);
        if (Interlocked.Exchange(ref _off, 1) == 0)
        {
            Reason = exception.GetType().Name + ": " + exception.Message;
        }

        if (logger is not null && Interlocked.Exchange(ref _logged, 1) == 0)
        {
            Log.FeatureOff(logger, exception, Name);
        }
    }

    /// <summary>Logs the features that went off before there was a logger (when the plugin's services were registered).</summary>
    /// <param name="logger">The logger.</param>
    /// <param name="features">The features to look at; all of them by default.</param>
    public static void LogUnlogged(ILogger logger, IEnumerable<Feature>? features = null)
    {
        foreach (var feature in features ?? All)
        {
            if (!feature.IsOn && Interlocked.Exchange(ref feature._logged, 1) == 0)
            {
                Log.FeatureOffEarly(logger, feature.Name, feature.Reason);
            }
        }
    }
}

/// <summary>The guard's log messages.</summary>
internal static partial class Log
{
    [LoggerMessage(Level = LogLevel.Error, Message = "quipclipper: {Feature} is off until Jellyfin restarts: this Jellyfin doesn't match what the plugin was built against. Update the plugin")]
    public static partial void FeatureOff(ILogger logger, Exception exception, string feature);

    [LoggerMessage(Level = LogLevel.Error, Message = "quipclipper: {Feature} is off until Jellyfin restarts ({Reason}): this Jellyfin doesn't match what the plugin was built against. Update the plugin")]
    public static partial void FeatureOffEarly(ILogger logger, string feature, string? reason);
}
