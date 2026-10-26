using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using Jellyfin.Plugin.Quipclipper.Server;
using Microsoft.Extensions.Logging;

namespace Jellyfin.Plugin.Quipclipper.Subtitles;

/// <summary>
/// The subtitle watcher's logic, apart from Jellyfin: media files Jellyfin added or updated
/// come in through <see cref="Notify"/>; once a burst is over, <see cref="RunAsync"/> compares
/// each one's subtitle files with the baseline and sends the ones that changed to quipclipper.
/// A library folder seen for the first time is looked through once to build its baseline,
/// which sends nothing. It never re-indexes anything itself: quipclipper's queue does that.
/// </summary>
public sealed class SubtitleWatch
{
    /// <summary>How long after the last item event a burst counts as over.</summary>
    public static readonly TimeSpan Quiet = TimeSpan.FromSeconds(10);

    /// <summary>The longest a changed file waits while events keep coming (a long scan).</summary>
    public static readonly TimeSpan MaxWait = TimeSpan.FromSeconds(30);

    private readonly SubtitleState _state;
    private readonly string _stateFile;
    private readonly IConfigStore _config;
    private readonly Func<QuipclipperClient> _client;
    private readonly Func<IReadOnlyList<WatchedLibrary>> _libraries;
    private readonly TimeProvider _time;
    private readonly ILogger _logger;
    private readonly ConcurrentDictionary<string, byte> _changed = new(StringComparer.Ordinal);
    private readonly Debounce _debounce = new(Quiet, MaxWait);
    private readonly object _lock = new();
    private string _token = string.Empty;
    private bool _stopped;
    private int _failures;
    private DateTimeOffset? _retryAt;

    /// <summary>
    /// Initializes a new instance of the <see cref="SubtitleWatch"/> class.
    /// </summary>
    /// <param name="stateFile">Where the state is kept.</param>
    /// <param name="config">The plugin's settings.</param>
    /// <param name="client">Makes a quipclipper client.</param>
    /// <param name="libraries">Jellyfin's libraries.</param>
    /// <param name="time">The clock.</param>
    /// <param name="logger">The logger.</param>
    public SubtitleWatch(string stateFile, IConfigStore config, Func<QuipclipperClient> client, Func<IReadOnlyList<WatchedLibrary>> libraries, TimeProvider time, ILogger logger)
    {
        _stateFile = stateFile;
        _state = SubtitleState.Load(stateFile);
        _config = config;
        _client = client;
        _libraries = libraries;
        _time = time;
        _logger = logger;
    }

    /// <summary>Raised when there is something to do sooner than the watcher planned (an item event).</summary>
    public event EventHandler? Woken;

    /// <summary>Gets the state (for the settings page and the tests).</summary>
    public SubtitleState State => _state;

    /// <summary>Gets when the last send happened and what it did.</summary>
    public (DateTimeOffset At, SendOutcome Outcome)? LastSend { get; private set; }

    /// <summary>Gets a value indicating whether a library folder's baseline is being built.</summary>
    public bool Seeding { get; private set; }

    /// <summary>Gets when <see cref="RunAsync"/> should run next; null when nothing waits.</summary>
    public DateTimeOffset? NextRunAt
    {
        get
        {
            DateTimeOffset? due;
            lock (_lock)
            {
                due = _debounce.DueAt;
            }

            if (!_stopped && _state.PendingCount > 0)
            {
                var retry = _retryAt ?? _time.GetUtcNow();
                due = due is null || retry < due ? retry : due;
            }

            return due;
        }
    }

    /// <summary>A media file was added or updated in Jellyfin. Cheap, and safe from any thread.</summary>
    /// <param name="mediaPath">Its path, as Jellyfin has it.</param>
    public void Notify(string mediaPath)
    {
        var config = _config.Current;
        if (!config.Connected || !config.WatchSubtitles || !SubtitleFiles.IsMedia(mediaPath))
        {
            return;
        }

        _changed[mediaPath] = 0;
        lock (_lock)
        {
            _debounce.Touch(_time.GetUtcNow());
        }

        Woken?.Invoke(this, EventArgs.Empty);
    }

    /// <summary>Does what is due: builds missing baselines, checks the files of a finished burst, sends what changed.</summary>
    /// <param name="cancellationToken">The cancellation token.</param>
    /// <returns>A task.</returns>
    public async Task RunAsync(CancellationToken cancellationToken)
    {
        var config = _config.Current;
        if (!config.Connected)
        {
            if (_state.Known > 0 || _state.Seeded.Count > 0 || _state.PendingCount > 0)
            {
                _state.Reset(); // disconnected: a later connection (maybe elsewhere) starts over
            }

            Forget();
            Save();
            return;
        }

        if (config.Token != _token)
        {
            _token = config.Token; // paired (again): sending may resume
            _stopped = false;
            _failures = 0;
            _retryAt = null;
        }

        if (!config.WatchSubtitles)
        {
            Forget();
            return;
        }

        var locations = WatchedLocations(config.LibraryIds);
        foreach (var location in locations.Where(l => !_state.IsSeeded(l)))
        {
            cancellationToken.ThrowIfCancellationRequested();
            Seed(location, cancellationToken);
            Save();
        }

        var now = _time.GetUtcNow();
        bool due;
        lock (_lock)
        {
            due = _debounce.IsDue(now);
            if (due)
            {
                _debounce.Clear();
            }
        }

        if (due)
        {
            foreach (var path in _changed.Keys.ToList())
            {
                _changed.TryRemove(path, out _);
                var location = locations.FirstOrDefault(l => Under(path, l));
                if (location is null)
                {
                    continue; // a library that isn't watched
                }

                var subtitles = SubtitleFiles.For(path);
                if (_state.IsSeeded(location))
                {
                    _state.Observe(path, subtitles);
                }
                else
                {
                    _state.Record(path, subtitles);
                }
            }

            Save();
        }

        if (_stopped || _state.PendingCount == 0 || (_retryAt is { } at && now < at)
            || QuipclipperClient.ParseServer(config.ServerUrl) is not { } server)
        {
            return;
        }

        var outcome = await ReindexSender.SendAsync(_state, _client(), server, config.Token, cancellationToken).ConfigureAwait(false);
        LastSend = (_time.GetUtcNow(), outcome);
        Save();
        if (outcome.Sent + outcome.Refused > 0)
        {
            Log.Sent(_logger, outcome.Sent, outcome.Refused);
        }

        switch (outcome.Kind)
        {
            case SendKind.Done:
                _failures = 0;
                _retryAt = null;
                break;
            case SendKind.Retry:
                _failures++;
                var pause = Backoff.After(_failures);
                _retryAt = _time.GetUtcNow() + pause;
                Log.SendFailed(_logger, outcome.Detail, _state.PendingCount, pause);
                break;
            case SendKind.Stopped:
                _stopped = true;
                Log.TokenRefused(_logger, outcome.Detail);
                var latest = _config.Current;
                if (latest.Token == config.Token)
                {
                    latest.LastError = outcome.Detail ?? "quipclipper refused the token: pair again.";
                    _config.Save(latest);
                }

                break;
        }
    }

    /// <summary>The watched libraries' folders, deepest first (so a nested folder wins).</summary>
    /// <param name="libraryIds">The picked libraries; none picks all.</param>
    /// <returns>The folders.</returns>
    public IReadOnlyList<string> WatchedLocations(IReadOnlyCollection<string> libraryIds)
    {
        ArgumentNullException.ThrowIfNull(libraryIds);
        var picked = new HashSet<string>(libraryIds, StringComparer.OrdinalIgnoreCase);
        return _libraries()
            .Where(lib => picked.Count == 0 || picked.Contains(lib.Id))
            .SelectMany(lib => lib.Locations)
            .Where(l => !string.IsNullOrWhiteSpace(l))
            .Select(l => l.Length > 1 ? l.TrimEnd('/', '\\') : l)
            .Distinct(StringComparer.Ordinal)
            .OrderByDescending(l => l.Length)
            .ToList();
    }

    private static bool Under(string path, string folder)
        => path.StartsWith(folder.TrimEnd('/', '\\') + Path.DirectorySeparatorChar, StringComparison.Ordinal)
           || path.StartsWith(folder.TrimEnd('/', '\\') + "/", StringComparison.Ordinal);

    private void Seed(string location, CancellationToken cancellationToken)
    {
        Seeding = true;
        var files = 0;
        try
        {
            foreach (var dir in SubtitleFiles.Folders(location))
            {
                cancellationToken.ThrowIfCancellationRequested();
                foreach (var (media, subtitles) in SubtitleFiles.InFolder(dir))
                {
                    _state.Record(media, subtitles);
                    files++;
                }
            }

            _state.MarkSeeded(location);
            Log.Seeded(_logger, location, files);
        }
        finally
        {
            Seeding = false;
        }
    }

    private void Forget()
    {
        _changed.Clear();
        lock (_lock)
        {
            _debounce.Clear();
        }
    }

    private void Save()
    {
        try
        {
            _state.SaveIfChanged(_stateFile);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            Log.StateNotSaved(_logger, ex, _stateFile);
        }
    }
}

/// <summary>A Jellyfin library: its id and its folders.</summary>
/// <param name="Id">The library's id.</param>
/// <param name="Name">The library's name.</param>
/// <param name="Locations">Its folders, as Jellyfin has them.</param>
public sealed record WatchedLibrary(string Id, string Name, IReadOnlyList<string> Locations);
