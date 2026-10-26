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

    private readonly ILibraryManager _libraryManager;
    private readonly SubtitleWatch _watch;
    private readonly Server.IConfigStore _config;
    private readonly TimeProvider _time;
    private readonly ILogger<SubtitleWatcher> _logger;
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
    {
        _libraryManager = libraryManager;
        _watch = watch;
        _config = config;
        _time = time;
        _logger = logger;
    }

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
    public Task StartAsync(CancellationToken cancellationToken)
    {
        _libraryManager.ItemAdded += OnItemChanged;
        _libraryManager.ItemUpdated += OnItemChanged;
        _watch.Woken += OnWoken;
        _config.Changed += OnWoken;
        _stop = new CancellationTokenSource();
        _loop = Task.Run(() => LoopAsync(_stop.Token), CancellationToken.None);
        return Task.CompletedTask;
    }

    /// <inheritdoc />
    public async Task StopAsync(CancellationToken cancellationToken)
    {
        _libraryManager.ItemAdded -= OnItemChanged;
        _libraryManager.ItemUpdated -= OnItemChanged;
        _watch.Woken -= OnWoken;
        _config.Changed -= OnWoken;
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

    private void OnItemChanged(object? sender, ItemChangeEventArgs e)
    {
        try
        {
            if (MediaPath(e?.Item) is { } path)
            {
                _watch.Notify(path);
            }
        }
#pragma warning disable CA1031 // nothing at all may reach Jellyfin's scan from here
        catch (Exception ex)
#pragma warning restore CA1031
        {
            Log.NotifyFailed(_logger, ex);
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
            await Task.Delay(StartDelay, _time, stop).ConfigureAwait(false);
        }
        catch (OperationCanceledException)
        {
            return;
        }

        while (!stop.IsCancellationRequested)
        {
            try
            {
                await _watch.RunAsync(stop).ConfigureAwait(false);
                var next = _watch.NextRunAt;
                var wait = next is { } at ? at - _time.GetUtcNow() : Timeout.InfiniteTimeSpan;
                if (wait != Timeout.InfiniteTimeSpan && wait < TimeSpan.FromSeconds(1))
                {
                    wait = TimeSpan.FromSeconds(1);
                }

                // Wake at the planned time, or sooner on an item event or a settings change.
                await _wake.WaitAsync(wait, stop).ConfigureAwait(false);
            }
            catch (OperationCanceledException) when (stop.IsCancellationRequested)
            {
                return;
            }
            catch (Exception ex) when (ex is not OperationCanceledException)
            {
                Log.WatchFailed(_logger, ex);
                try
                {
                    await Task.Delay(TimeSpan.FromMinutes(1), _time, stop).ConfigureAwait(false);
                }
                catch (OperationCanceledException)
                {
                    return;
                }
            }
        }
    }
}
