using Jellyfin.Plugin.Quipclipper.Subtitles;

namespace Jellyfin.Plugin.Quipclipper.Tests;

public class SubtitleStateTests
{
    private static Dictionary<string, long> Subs(params (string Name, long Ticks)[] files) => files.ToDictionary(f => f.Name, f => f.Ticks);

    [Fact]
    public void Observe_queues_only_a_real_change()
    {
        var s = new SubtitleState();
        s.Record("/shows/a.mkv", Subs(("a.en.srt", 1)));
        Assert.False(s.Observe("/shows/a.mkv", Subs(("a.en.srt", 1))));      // the same
        Assert.True(s.Observe("/shows/a.mkv", Subs(("a.en.srt", 2))));       // rewritten
        Assert.True(s.Observe("/shows/b.mkv", Subs(("b.en.srt", 1))));       // new file with subtitles
        Assert.False(s.Observe("/shows/c.mkv", Subs()));                     // new file without: nothing to index
        Assert.True(s.Observe("/shows/a.mkv", Subs()));                      // removed
        Assert.Equal(["/shows/a.mkv", "/shows/b.mkv"], s.NextBatch(10));     // queued once each, oldest first
        Assert.Equal(1, s.Known);                                             // only files that have subtitles are kept
    }

    [Fact]
    public void Sent_files_leave_the_waiting_list()
    {
        var s = new SubtitleState();
        foreach (var n in new[] { "a", "b", "c" })
        {
            s.Observe($"/m/{n}.mkv", Subs(($"{n}.srt", 1)));
        }

        Assert.Equal(["/m/a.mkv", "/m/b.mkv"], s.NextBatch(2));
        s.Sent(["/m/a.mkv", "/m/b.mkv"]);
        Assert.Equal(["/m/c.mkv"], s.NextBatch(2));
        Assert.Equal(1, s.PendingCount);
    }

    [Fact]
    public void Past_the_cap_the_oldest_waiting_files_are_dropped()
    {
        var s = new SubtitleState();
        for (var i = 0; i <= SubtitleState.MaxPending; i++)
        {
            s.Observe($"/m/{i}.mkv", Subs(("x.srt", 1)));
        }

        Assert.Equal(SubtitleState.MaxPending, s.PendingCount);
        Assert.Equal(1, s.Dropped);
        Assert.Equal("/m/1.mkv", s.NextBatch(1)[0]);
    }

    [Fact]
    public void The_state_survives_a_restart()
    {
        using var dir = new TempDir();
        var file = Path.Combine(dir.Path, "state", "s.json");
        var s = new SubtitleState();
        s.MarkSeeded("/shows");
        s.Record("/shows/a.mkv", Subs(("a.en.srt", 5)));
        s.Observe("/shows/b.mkv", Subs(("b.en.srt", 6)));
        Assert.True(s.SaveIfChanged(file));
        Assert.False(s.SaveIfChanged(file));   // nothing changed since

        var back = SubtitleState.Load(file);
        Assert.True(back.IsSeeded("/shows"));
        Assert.Equal(2, back.Known);
        Assert.Equal(["/shows/b.mkv"], back.NextBatch(10));
        Assert.False(back.Observe("/shows/a.mkv", Subs(("a.en.srt", 5))));
    }

    [Theory]
    [InlineData(null)]
    [InlineData("not json")]
    [InlineData("{\"Version\": 99, \"Pending\": [\"/x.mkv\"]}")]
    public void A_missing_or_damaged_file_starts_over(string? content)
    {
        using var dir = new TempDir();
        var file = Path.Combine(dir.Path, "s.json");
        if (content is not null)
        {
            File.WriteAllText(file, content);
        }

        var s = SubtitleState.Load(file);
        Assert.Equal(0, s.PendingCount);
        Assert.Empty(s.Seeded);
    }

    [Fact]
    public void Reset_forgets_everything()
    {
        var s = new SubtitleState();
        s.MarkSeeded("/shows");
        s.Observe("/shows/b.mkv", Subs(("b.en.srt", 6)));
        s.Reset();
        Assert.Equal((0, 0), (s.PendingCount, s.Known));
        Assert.False(s.IsSeeded("/shows"));
    }
}

public class TimingTests
{
    private static readonly DateTimeOffset T0 = new(2026, 10, 26, 12, 0, 0, TimeSpan.Zero);

    [Fact]
    public void A_burst_is_over_after_a_quiet_spell()
    {
        var d = new Debounce(TimeSpan.FromSeconds(10), TimeSpan.FromSeconds(30));
        Assert.Null(d.DueAt);
        d.Touch(T0);
        d.Touch(T0.AddSeconds(4));
        Assert.Equal(T0.AddSeconds(14), d.DueAt);
        Assert.False(d.IsDue(T0.AddSeconds(13)));
        Assert.True(d.IsDue(T0.AddSeconds(14)));
        d.Clear();
        Assert.Null(d.DueAt);
    }

    [Fact]
    public void A_burst_that_goes_on_is_handled_after_the_longest_wait()
    {
        var d = new Debounce(TimeSpan.FromSeconds(10), TimeSpan.FromSeconds(30));
        for (var s = 0; s <= 60; s += 5)
        {
            d.Touch(T0.AddSeconds(s));
        }

        Assert.Equal(T0.AddSeconds(30), d.DueAt);
    }

    [Fact]
    public void Backoff_doubles_up_to_ten_minutes()
    {
        Assert.Equal(
            [30, 60, 120, 240, 480, 600, 600],
            Enumerable.Range(1, 7).Select(n => (int)Backoff.After(n).TotalSeconds));
        Assert.Equal(TimeSpan.FromMinutes(10), Backoff.After(1000));
    }
}
