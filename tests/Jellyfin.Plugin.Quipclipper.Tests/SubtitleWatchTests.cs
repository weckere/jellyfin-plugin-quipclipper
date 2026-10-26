using Jellyfin.Plugin.Quipclipper.Subtitles;
using MediaBrowser.Controller.Entities;
using MediaBrowser.Controller.Entities.Audio;
using MediaBrowser.Controller.Entities.Movies;
using Microsoft.Extensions.Logging.Abstractions;

namespace Jellyfin.Plugin.Quipclipper.Tests;

/// <summary>
/// The watcher end to end against a library folder on disk, a fake quipclipper and a clock
/// moved by hand: nothing here waits on real time.
/// </summary>
public sealed class SubtitleWatchTests : IDisposable
{
    private readonly TempDir _dir = new();
    private readonly FakeQuipclipper _qc = new();
    private readonly ManualClock _clock = new();
    private readonly MemoryConfig _config = MemoryConfig.Connected();
    private readonly string _shows;
    private readonly string _movies;
    private readonly string _ep;

    public SubtitleWatchTests()
    {
        _ep = _dir.File("shows/TNG/Season 3/TNG.S03E26.mkv", string.Empty);
        _dir.File("shows/TNG/Season 3/TNG.S03E26.en.srt", "old");
        _dir.File("movies/Bambi (1942)/Bambi.mkv", string.Empty);
        _shows = Path.Combine(_dir.Path, "shows");
        _movies = Path.Combine(_dir.Path, "movies");
    }

    private string StateFile => Path.Combine(_dir.Path, "config", "quipclipper-subtitles.json");

    public void Dispose() => _dir.Dispose();

    private SubtitleWatch Watch() => new(
        StateFile, _config, _qc.Client,
        () => [new WatchedLibrary("lib-shows", "Shows", [_shows]), new WatchedLibrary("lib-movies", "Movies", [_movies + "/"])],
        _clock, NullLogger.Instance);

    private async Task Settle(SubtitleWatch w)
    {
        _clock.Advance(SubtitleWatch.Quiet);
        await w.RunAsync(default);
    }

    [Fact]
    public async Task The_first_run_builds_the_baseline_and_sends_nothing()
    {
        var w = Watch();
        await w.RunAsync(default);
        Assert.Empty(_qc.Requests);
        Assert.True(w.State.IsSeeded(_shows) && w.State.IsSeeded(_movies));
        Assert.Equal(1, w.State.Known);

        w.Notify(_ep);  // an update with no subtitle change: still nothing
        await Settle(w);
        Assert.Empty(_qc.Requests);
    }

    [Fact]
    public async Task A_new_subtitle_is_sent_once_the_burst_is_over()
    {
        var w = Watch();
        await w.RunAsync(default);
        var bambi = Path.Combine(_movies, "Bambi (1942)", "Bambi.mkv");
        _dir.File("movies/Bambi (1942)/Bambi.en.srt", "new");
        _dir.File("shows/TNG/Season 3/TNG.S03E26.de.srt", "neu");
        w.Notify(bambi);
        w.Notify(_ep);
        w.Notify(_ep);
        Assert.Equal(_clock.GetUtcNow() + SubtitleWatch.Quiet, w.NextRunAt);

        _clock.Advance(TimeSpan.FromSeconds(9));
        await w.RunAsync(default);   // still inside the burst
        Assert.Empty(_qc.Requests);

        _clock.Advance(TimeSpan.FromSeconds(1));
        await w.RunAsync(default);
        Assert.Equal([new[] { _ep, bambi }.Order(StringComparer.Ordinal).ToArray()], _qc.Queued.Select(q => q.Order(StringComparer.Ordinal).ToArray()).ToList());
        Assert.Equal(0, w.State.PendingCount);
        Assert.Null(w.NextRunAt);
    }

    [Fact]
    public async Task A_rewritten_or_removed_subtitle_is_sent_too()
    {
        var w = Watch();
        await w.RunAsync(default);
        var srt = Path.Combine(Path.GetDirectoryName(_ep)!, "TNG.S03E26.en.srt");
        File.SetLastWriteTimeUtc(srt, DateTime.UtcNow.AddMinutes(5));
        w.Notify(_ep);
        await Settle(w);
        File.Delete(srt);
        w.Notify(_ep);
        await Settle(w);
        Assert.Equal([[_ep], [_ep]], _qc.Queued);
    }

    [Fact]
    public async Task Only_the_picked_libraries_are_watched()
    {
        _config.Current.LibraryIds = ["lib-movies"];
        var w = Watch();
        await w.RunAsync(default);
        Assert.False(w.State.IsSeeded(_shows));
        _dir.File("shows/TNG/Season 3/TNG.S03E26.de.srt", "neu");
        w.Notify(_ep);
        w.Notify("/elsewhere/x.mkv");
        await Settle(w);
        Assert.Empty(_qc.Requests);
    }

    [Fact]
    public async Task A_library_picked_later_gets_its_baseline_first()
    {
        _config.Current.LibraryIds = ["lib-movies"];
        var w = Watch();
        await w.RunAsync(default);
        _config.Current.LibraryIds = [];
        w.Notify(_ep);  // its existing subtitle isn't news
        await Settle(w);
        Assert.True(w.State.IsSeeded(_shows));
        Assert.Empty(_qc.Requests);
    }

    [Fact]
    public async Task While_quipclipper_is_down_files_wait_and_are_retried_with_growing_pauses()
    {
        var w = Watch();
        await w.RunAsync(default);
        _qc.Down = true;
        _dir.File("shows/TNG/Season 3/TNG.S03E26.de.srt", "neu");
        w.Notify(_ep);
        await Settle(w);
        Assert.Equal(1, w.State.PendingCount);
        Assert.Equal(_clock.GetUtcNow() + Backoff.First, w.NextRunAt);

        await w.RunAsync(default);  // too early: nothing is tried
        Assert.Single(_qc.Requests);
        _clock.Advance(Backoff.First);
        await w.RunAsync(default);  // tried, fails again: the pause doubles
        Assert.Equal(2, _qc.Requests.Count);
        Assert.Equal(_clock.GetUtcNow() + Backoff.After(2), w.NextRunAt);

        _qc.Down = false;
        _clock.Advance(Backoff.After(2));
        await w.RunAsync(default);
        Assert.Equal([[_ep]], _qc.Queued);
        Assert.Equal(0, w.State.PendingCount);
    }

    [Fact]
    public async Task What_waits_survives_a_restart()
    {
        var w = Watch();
        await w.RunAsync(default);
        _qc.Down = true;
        _dir.File("shows/TNG/Season 3/TNG.S03E26.de.srt", "neu");
        w.Notify(_ep);
        await Settle(w);

        _qc.Down = false;
        var again = Watch();
        await again.RunAsync(default);
        Assert.Equal([[_ep]], _qc.Queued);
    }

    [Fact]
    public async Task A_revoked_token_stops_sending_until_this_server_pairs_again()
    {
        var w = Watch();
        await w.RunAsync(default);
        _qc.Revoked = true;
        _dir.File("shows/TNG/Season 3/TNG.S03E26.de.srt", "neu");
        w.Notify(_ep);
        await Settle(w);
        Assert.Contains("401", _config.Current.LastError, StringComparison.Ordinal);
        Assert.Null(w.NextRunAt);
        _clock.Advance(TimeSpan.FromHours(1));
        await w.RunAsync(default);
        Assert.Single(_qc.Requests);  // no more tries

        _qc.Revoked = false;
        var config = _config.Current;
        config.Token = FakeQuipclipper.Token + "-new";  // paired again
        config.LastError = string.Empty;
        _config.Save(config);
        _qc.Revoked = false;
        await w.RunAsync(default);
        Assert.Equal(2, _qc.Requests.Count);  // resumed (the fake knows only the old token: 401 again)
    }

    [Fact]
    public async Task Disconnecting_forgets_the_baseline_and_ignores_events()
    {
        var w = Watch();
        await w.RunAsync(default);
        var config = _config.Current;
        config.Token = string.Empty;
        _config.Save(config);
        await w.RunAsync(default);
        Assert.Equal((0, 0), (w.State.Known, w.State.Seeded.Count));
        w.Notify(_ep);
        Assert.Null(w.NextRunAt);
    }

    [Fact]
    public async Task Watching_off_sends_nothing()
    {
        _config.Current.WatchSubtitles = false;
        var w = Watch();
        await w.RunAsync(default);
        _dir.File("shows/TNG/Season 3/TNG.S03E26.de.srt", "neu");
        w.Notify(_ep);
        await Settle(w);
        Assert.Empty(_qc.Requests);
        Assert.Null(w.NextRunAt);
    }

    [Fact]
    public void Only_video_and_audio_files_on_disk_are_looked_at()
    {
        Assert.Equal("/m/a.mkv", SubtitleWatcher.MediaPath(new Movie { Path = "/m/a.mkv" }));
        Assert.Equal("/m/a.mkv", SubtitleWatcher.MediaPath(new Video { Path = "/m/a.mkv" }));
        Assert.Equal("/p/a.mp3", SubtitleWatcher.MediaPath(new Audio { Path = "/p/a.mp3" }));
        Assert.Null(SubtitleWatcher.MediaPath(new Video { Path = "/m/a.iso" }));
        Assert.Null(SubtitleWatcher.MediaPath(new Folder { Path = "/m" }));
        Assert.Null(SubtitleWatcher.MediaPath(null));
    }
}
