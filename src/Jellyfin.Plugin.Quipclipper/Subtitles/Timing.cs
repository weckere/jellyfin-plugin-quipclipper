using System;

namespace Jellyfin.Plugin.Quipclipper.Subtitles;

/// <summary>
/// When a burst of item events is over: <see cref="Quiet"/> after the last one, but never
/// later than <see cref="MaxWait"/> after the first, so a long library scan still sends what
/// it found as it goes. Pure arithmetic on the times it is given; the watcher does the waiting.
/// </summary>
public sealed class Debounce
{
    private DateTimeOffset? _first;
    private DateTimeOffset? _last;

    /// <summary>
    /// Initializes a new instance of the <see cref="Debounce"/> class.
    /// </summary>
    /// <param name="quiet">How long no event must come.</param>
    /// <param name="maxWait">The longest a first event waits.</param>
    public Debounce(TimeSpan quiet, TimeSpan maxWait)
    {
        Quiet = quiet;
        MaxWait = maxWait;
    }

    /// <summary>Gets how long no event must come.</summary>
    public TimeSpan Quiet { get; }

    /// <summary>Gets the longest a first event waits.</summary>
    public TimeSpan MaxWait { get; }

    /// <summary>Gets when the burst counts as over; null with nothing waiting.</summary>
    public DateTimeOffset? DueAt => _first is { } first && _last is { } last
        ? (last + Quiet < first + MaxWait ? last + Quiet : first + MaxWait)
        : null;

    /// <summary>An event came.</summary>
    /// <param name="now">When.</param>
    /// <returns>True when it starts a burst (nothing was waiting): only then does the watcher need waking.</returns>
    public bool Touch(DateTimeOffset now)
    {
        var started = _first is null;
        _first ??= now;
        _last = now;
        return started;
    }

    /// <summary>Whether the burst is over.</summary>
    /// <param name="now">The time now.</param>
    /// <returns>True when something waits and its time has come.</returns>
    public bool IsDue(DateTimeOffset now) => DueAt is { } due && due <= now;

    /// <summary>The burst was handled.</summary>
    public void Clear()
    {
        _first = null;
        _last = null;
    }
}

/// <summary>The pause before trying quipclipper again: doubling from <see cref="First"/> up to <see cref="Max"/>.</summary>
public static class Backoff
{
    /// <summary>The first pause.</summary>
    public static readonly TimeSpan First = TimeSpan.FromSeconds(30);

    /// <summary>The longest pause.</summary>
    public static readonly TimeSpan Max = TimeSpan.FromMinutes(10);

    /// <summary>The pause after the <paramref name="failures"/>th failure in a row.</summary>
    /// <param name="failures">Failures in a row, from 1.</param>
    /// <returns>The pause.</returns>
    public static TimeSpan After(int failures)
    {
        var shift = Math.Clamp(failures - 1, 0, 20);
        var ticks = First.Ticks * (1L << shift);
        return ticks >= Max.Ticks || ticks <= 0 ? Max : TimeSpan.FromTicks(ticks);
    }
}
