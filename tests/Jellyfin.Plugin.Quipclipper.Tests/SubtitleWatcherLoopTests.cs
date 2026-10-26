using System.Collections.Concurrent;
using Jellyfin.Plugin.Quipclipper.Subtitles;
using MediaBrowser.Controller.Entities;
using MediaBrowser.Controller.Library;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Time.Testing;
using Moq;

namespace Jellyfin.Plugin.Quipclipper.Tests;

/// <summary>
/// The watcher's hosted loop (<see cref="SubtitleWatcher"/>), started as Jellyfin starts it,
/// on a clock the test moves. The loop says each time it starts to wait, and for how long;
/// the test moves the clock only then, so nothing depends on real time or on how fast the
/// machine is (the long timeout on that signal only stops a broken loop hanging the run).
/// </summary>
public sealed class SubtitleWatcherLoopTests : IDisposable
{
    private readonly TempDir _dir = new();
    private readonly FakeQuipclipper _qc = new();
    private readonly MemoryConfig _config = MemoryConfig.Connected();
    private readonly FakeTimeProvider _clock = new(new DateTimeOffset(2026, 10, 26, 12, 0, 0, TimeSpan.Zero));
    private readonly Mock<ILibraryManager> _library = new();
    private readonly ConcurrentQueue<TimeSpan> _naps = new();
    private readonly SemaphoreSlim _napped = new(0);
    private readonly string _shows;
    private readonly string _episode;

    public SubtitleWatcherLoopTests()
    {
        _episode = _dir.File("shows/TNG/Season 3/TNG.S03E26.mkv", string.Empty);
        _dir.File("shows/TNG/Season 3/TNG.S03E26.en.srt", "old");
        _shows = Path.Combine(_dir.Path, "shows");
    }

    public void Dispose()
    {
        _napped.Dispose();
        _dir.Dispose();
    }

    private (SubtitleWatcher Watcher, SubtitleWatch Watch) Start(Func<IReadOnlyList<WatchedLibrary>>? libraries = null, Feature? feature = null)
    {
        var watch = new SubtitleWatch(
            Path.Combine(_dir.Path, "state.json"), _config, _qc.Client,
            libraries ?? (() => [new WatchedLibrary("lib-shows", "Shows", [_shows])]),
            _clock, NullLogger.Instance);
        var watcher = new SubtitleWatcher(_library.Object, watch, _config, _clock, NullLogger<SubtitleWatcher>.Instance, feature ?? new Feature("subtitle re-index"));
        watcher.Napping += wait =>
        {
            _naps.Enqueue(wait);
            _napped.Release();
        };
        return (watcher, watch);
    }

    /// <summary>The next wait the loop starts.</summary>
    private async Task<TimeSpan> Nap()
    {
        Assert.True(await _napped.WaitAsync(TimeSpan.FromMinutes(1)), "the loop never started to wait");
        Assert.True(_naps.TryDequeue(out var wait));
        return wait;
    }

    [Fact]
    public async Task The_loop_waits_for_Jellyfin_to_start_builds_the_baseline_and_sends_a_finished_burst()
    {
        var (watcher, watch) = Start();
        using (watcher)
        {
            await watcher.StartAsync(default);
            Assert.Equal(SubtitleWatcher.StartDelay, await Nap());
            Assert.False(watch.State.IsSeeded(_shows));

            _clock.Advance(SubtitleWatcher.StartDelay);
            Assert.Equal(Timeout.InfiniteTimeSpan, await Nap()); // the baseline is built; nothing waits
            Assert.True(watch.State.IsSeeded(_shows));
            Assert.Empty(_qc.Requests);

            // Jellyfin downloads a subtitle: its item update wakes the loop, which waits for the burst to end.
            _dir.File("shows/TNG/Season 3/TNG.S03E26.de.srt", "neu");
            _library.Raise(l => l.ItemUpdated += null, _library.Object, new ItemChangeEventArgs { Item = new Video { Path = _episode } });
            Assert.Equal(SubtitleWatch.Quiet, await Nap());
            Assert.Empty(_qc.Requests);

            _clock.Advance(SubtitleWatch.Quiet);
            Assert.Equal(Timeout.InfiniteTimeSpan, await Nap());
            Assert.Equal([[_episode]], _qc.Queued);

            await watcher.StopAsync(default);
            Assert.True(watcher.Loop!.IsCompletedSuccessfully);
        }
    }

    [Fact]
    public async Task A_run_that_fails_pauses_and_the_loop_carries_on()
    {
        var calls = 0;
        var (watcher, watch) = Start(() => ++calls == 1
            ? throw new InvalidOperationException("the library is busy")
            : [new WatchedLibrary("lib-shows", "Shows", [_shows])]);
        using (watcher)
        {
            await watcher.StartAsync(default);
            Assert.Equal(SubtitleWatcher.StartDelay, await Nap());
            _clock.Advance(SubtitleWatcher.StartDelay);
            Assert.Equal(SubtitleWatcher.FailurePause, await Nap());
            Assert.False(watch.State.IsSeeded(_shows));

            _clock.Advance(SubtitleWatcher.FailurePause);
            Assert.Equal(Timeout.InfiniteTimeSpan, await Nap());
            Assert.True(watch.State.IsSeeded(_shows));
            await watcher.StopAsync(default);
        }
    }

    [Fact]
    public async Task A_mismatch_in_a_run_ends_the_loop_and_turns_the_feature_off()
    {
        var feature = new Feature("subtitle re-index");
        var (watcher, _) = Start(() => throw new MissingMethodException("ILibraryManager", "GetVirtualFolders"), feature);
        using (watcher)
        {
            await watcher.StartAsync(default);
            Assert.Equal(SubtitleWatcher.StartDelay, await Nap());
            _clock.Advance(SubtitleWatcher.StartDelay);
            await watcher.Loop!.WaitAsync(TimeSpan.FromMinutes(1));
            Assert.False(feature.IsOn);
            Assert.True(_naps.IsEmpty);
            await watcher.StopAsync(default);
        }
    }

    [Fact]
    public async Task Stopping_ends_a_wait_at_once()
    {
        var (watcher, _) = Start();
        using (watcher)
        {
            await watcher.StartAsync(default);
            Assert.Equal(SubtitleWatcher.StartDelay, await Nap());
            await watcher.StopAsync(default);
            Assert.True(watcher.Loop!.IsCompletedSuccessfully);
        }
    }
}
