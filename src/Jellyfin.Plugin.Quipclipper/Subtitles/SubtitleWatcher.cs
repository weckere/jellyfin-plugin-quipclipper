using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using MediaBrowser.Controller.Entities;
using MediaBrowser.Controller.Library;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;

namespace Jellyfin.Plugin.Quipclipper.Subtitles;

/// <summary>
/// Jellyfin's side of the subtitle watcher: notes every video and audio file Jellyfin adds or
/// updates (its own subtitle downloads, its subtitle dialog and its folder monitor all end in
/// an item update) and runs <see cref="SubtitleWatch"/> on a background loop of its own, so
/// nothing it does runs inside, or can fail, a library scan.
/// </summary>
public sealed class SubtitleWatcher : IHostedService, IDisposable
{
    /// <summary>How long after Jellyfin starts the watcher first looks (Jellyfin is busy starting).</summary>
    public static readonly TimeSpan StartDelay = TimeSpan.FromSeconds(30);

    /// <summary>How long the loop pauses after a run failed, before it carries on.</summary>
    public static readonly TimeSpan FailurePause = TimeSpan.FromMinutes(1);

    private readonly ILibraryManager _libraryManager;
    private readonly SubtitleWatch _watch;
    private readonly Server.IConfigStore _config;
    private readonly TimeProvider _time;
    private readonly ILogger<SubtitleWatcher> _logger;
    private readonly Feature _feature;
    private readonly SemaphoreSlim _wake = new(0, 1);
    private CancellationTokenSource? _stop;
    private Task? _loop;

    /// <summary>
    /// Initializes a new instance of the <see cref="SubtitleWatcher"/> class.
    /// </summary>
    /// <param name="libraryManager">Jellyfin's library.</param>
    /// <param name="watch">The watcher's logic.</param>
    /// <param name="config">The plugin's settings.</param>
    /// <param name="time">The clock.</param>
    /// <param name="logger">The logger.</param>
    public SubtitleWatcher(ILibraryManager libraryManager, SubtitleWatch watch, Server.IConfigStore config, TimeProvider time, ILogger<SubtitleWatcher> logger)
        : this(libraryManager, watch, config, time, logger, Feature.SubtitleReindex)
    {
    }

    internal SubtitleWatcher(ILibraryManager libraryManager, SubtitleWatch watch, Server.IConfigStore config, TimeProvider time, ILogger<SubtitleWatcher> logger, Feature feature)
    {
        _libraryManager = libraryManager;
        _watch = watch;
        _config = config;
        _time = time;
        _logger = logger;
        _feature = feature;
    }

    /// <summary>Gets the loop while it runs (the tests wait for it to end).</summary>
    internal Task? Loop => _loop;

    /// <summary>Raised as the loop starts to wait, with how long (the tests move their clock then).</summary>
    internal event Action<TimeSpan>? Napping;

    /// <summary>Jellyfin's libraries, as the watcher needs them.</summary>
    /// <param name="libraryManager">Jellyfin's library.</param>
    /// <returns>Each library's id, name and folders.</returns>
    public static IReadOnlyList<WatchedLibrary> Libraries(ILibraryManager libraryManager)
    {
        ArgumentNullException.ThrowIfNull(libraryManager);
        return libraryManager.GetVirtualFolders()
            .Where(f => !string.IsNullOrEmpty(f.ItemId))
            .Select(f => new WatchedLibrary(f.ItemId, f.Name ?? string.Empty, f.Locations ?? []))
            .ToList();
    }

    /// <summary>Whether an item is a media file the watcher looks at: a video or audio file on disk.</summary>
    /// <param name="item">The item.</param>
    /// <returns>Its path, or null.</returns>
    public static string? MediaPath(BaseItem? item)
    {
        if (item is null || item.IsFolder || item.IsVirtualItem || item is not (Video or MediaBrowser.Controller.Entities.Audio.Audio))
        {
            return null;
        }

        return SubtitleFiles.IsMedia(item.Path) ? item.Path : null;
    }

    /// <inheritdoc />
    /// <remarks>Never throws: Jellyfin starts every hosted service in turn, and one that throws stops the server starting.</remarks>
    public Task StartAsync(CancellationToken cancellationToken)
    {
        try
        {
            if (!_feature.Run(_logger, Subscribe, false))
            {
                return Task.CompletedTask;
            }
        }
#pragma warning disable CA1031 // nothing may stop Jellyfin starting
        catch (Exception ex)
#pragma warning restore CA1031
        {
            Log.WatchFailed(_logger, ex);
            return Task.CompletedTask;
        }

        _stop = new CancellationTokenSource();
        _loop = Task.Run(() => LoopAsync(_stop.Token), CancellationToken.None);
        return Task.CompletedTask;
    }

    /// <inheritdoc />
    public async Task StopAsync(CancellationToken cancellationToken)
    {
        try
        {
            Unsubscribe(); // also when the feature went off: what was subscribed comes off
        }
#pragma warning disable CA1031 // stopping goes on regardless
        catch (Exception ex)
#pragma warning restore CA1031
        {
            if (Feature.IsMismatch(ex))
            {
                _feature.TurnOff(_logger, ex);
            }
            else
            {
                Log.WatchFailed(_logger, ex);
            }
        }

        if (_stop is null || _loop is null)
        {
            return;
        }

        await _stop.CancelAsync().ConfigureAwait(false);
        try
        {
            await _loop.WaitAsync(cancellationToken).ConfigureAwait(false);
        }
        catch (OperationCanceledException)
        {
        }
    }

    /// <inheritdoc />
    public void Dispose()
    {
        _stop?.Dispose();
        _wake.Dispose();
    }

    private bool Subscribe()
    {
        _libraryManager.ItemAdded += OnItemChanged;
        _libraryManager.ItemUpdated += OnItemChanged;
        _watch.Woken += OnWoken;
        _config.Changed += OnWoken;
        return true;
    }

    private void Unsubscribe()
    {
        _libraryManager.ItemAdded -= OnItemChanged;
        _libraryManager.ItemUpdated -= OnItemChanged;
        _watch.Woken -= OnWoken;
        _config.Changed -= OnWoken;
    }

    private void OnItemChanged(object? sender, ItemChangeEventArgs e)
    {
        try
        {
            _feature.Run(_logger, () => Note(e));
        }
#pragma warning disable CA1031 // nothing at all may reach Jellyfin's scan from here
        catch (Exception ex)
#pragma warning restore CA1031
        {
            Log.NotifyFailed(_logger, ex);
        }
    }

    private void Note(ItemChangeEventArgs? e)
    {
        if (MediaPath(e?.Item) is { } path)
        {
            _watch.Notify(path);
        }
    }

    private void OnWoken(object? sender, EventArgs e)
    {
        try
        {
            if (_wake.CurrentCount == 0)
            {
                _wake.Release();
            }
        }
        catch (Exception ex) when (ex is SemaphoreFullException or ObjectDisposedException)
        {
            // already woken, or stopping
        }
    }

    private async Task LoopAsync(CancellationToken stop)
    {
        try
        {
            await NapAsync(StartDelay, false, stop).ConfigureAwait(false);
            while (_feature.IsOn)
            {
                TimeSpan wait;
                try
                {
                    await _watch.RunAsync(stop).ConfigureAwait(false);
                    wait = UntilNextRun();
                }
                catch (OperationCanceledException) when (stop.IsCancellationRequested)
                {
                    return;
                }
                catch (Exception ex) when (Feature.IsMismatch(ex))
                {
                    _feature.TurnOff(_logger, ex); // and the loop ends: nothing it does can work
                    return;
                }
                catch (Exception ex) when (ex is not OperationCanceledException)
                {
                    Log.WatchFailed(_logger, ex);
                    await NapAsync(FailurePause, false, stop).ConfigureAwait(false);
                    continue;
                }

                // Wake at the planned time, or sooner on an item event or a settings change.
                await NapAsync(wait, true, stop).ConfigureAwait(false);
            }
        }
        catch (OperationCanceledException) when (stop.IsCancellationRequested)
        {
        }
    }

    private TimeSpan UntilNextRun()
    {
        var wait = _watch.NextRunAt is { } at ? at - _time.GetUtcNow() : Timeout.InfiniteTimeSpan;
        return wait != Timeout.InfiniteTimeSpan && wait < TimeSpan.FromSeconds(1) ? TimeSpan.FromSeconds(1) : wait;
    }

    // Waits on Jellyfin's clock (the tests' clock), and on a wake-up when wakeable.
    private async Task NapAsync(TimeSpan wait, bool wakeable, CancellationToken stop)
    {
        using var nap = CancellationTokenSource.CreateLinkedTokenSource(stop);
        var timer = wait == Timeout.InfiniteTimeSpan
            ? Task.Delay(Timeout.InfiniteTimeSpan, nap.Token)
            : Task.Delay(wait, _time, nap.Token);
        var woken = wakeable ? _wake.WaitAsync(nap.Token) : null;
        Napping?.Invoke(wait);
        if (woken is null)
        {
            await timer.ConfigureAwait(false);
            return;
        }

        await Task.WhenAny(timer, woken).ConfigureAwait(false);
        await nap.CancelAsync().ConfigureAwait(false);
        stop.ThrowIfCancellationRequested();
    }
}
