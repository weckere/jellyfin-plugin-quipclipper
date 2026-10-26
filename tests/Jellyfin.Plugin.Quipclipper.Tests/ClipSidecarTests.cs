using Jellyfin.Plugin.Quipclipper.Clips;

namespace Jellyfin.Plugin.Quipclipper.Tests;

public class ClipSidecarTests
{
    internal static string Fixture(string name) => File.ReadAllText(Path.Combine(AppContext.BaseDirectory, "Fixtures", name));

    [Fact]
    public void ParsesABackfilledEpisodeClip()
    {
        var s = ClipSidecar.Parse(Fixture("episode-backfill.qc.json"))!;
        Assert.Equal(1, s.Version);
        Assert.Equal("video", s.Kind);
        Assert.Equal("backfill", s.Origin);
        Assert.Equal("No it doesn't really matter", s.Cue);
        Assert.Equal(new DateTimeOffset(2026, 6, 21, 9, 33, 0, TimeSpan.Zero).Date, s.Created!.Value.Date);
        Assert.Equal(46.0, s.Clip!.Start);
        Assert.Null(s.Clip.End);
        Assert.Null(s.Line);
        Assert.Equal(Guid.Parse("efddad91d03484dcaf5d941d31f6ff3a"), s.Jellyfin!.Id);
        Assert.Equal("Episode", s.Jellyfin.Type);
        Assert.Equal("Crank Yankers", s.Jellyfin.Show);
        Assert.Equal((1, 1, 2002), (s.Jellyfin.Season, s.Jellyfin.Episode, s.Jellyfin.Year));
        Assert.Equal("Tenacious D & Wanda Sykes", s.Jellyfin.Title);
    }

    [Fact]
    public void ParsesAMovieClipWithItsLine()
    {
        var s = ClipSidecar.Parse(Fixture("movie-clip.qc.json"))!;
        Assert.Equal("clip", s.Origin);
        Assert.Equal("Winter sure is long, isn't it?", s.Cue);
        Assert.Equal(2419.819, s.Line!.Start, 3);
        Assert.Equal(2428.258, s.Line.End!.Value, 3);
        Assert.Equal("Movie", s.Jellyfin!.Type);
        Assert.Null(s.Jellyfin.Season);
        Assert.Equal(1942, s.Jellyfin.Year);
        Assert.Equal("Bambi (1942)", s.SourceName);
    }

    [Fact]
    public void ToleratesNullsWhereABackfillCouldNotKnow()
    {
        var s = ClipSidecar.Parse(Fixture("no-cue-backfill.qc.json"))!;
        Assert.Null(s.Cue);
        Assert.Null(s.Jellyfin);
        Assert.Null(s.Line);
        Assert.Equal(new ParsedName(3, 26, null), s.Parsed);
        Assert.Equal("Star Trek The Next Generation", s.SourceName);
    }

    [Fact]
    public void ParsesASet()
    {
        var s = ClipSidecar.Parse(Fixture("set.qc.json"))!;
        Assert.Equal("set", s.Kind);
        Assert.Equal("101 Dalmatians (1961)", s.Name);
    }

    [Theory]
    [InlineData("")]
    [InlineData("not json")]
    [InlineData("{\"schema\": \"quipclipper.clip-meta\"")]
    [InlineData("[]")]
    [InlineData("null")]
    [InlineData("{}")]
    [InlineData("{\"schema\": \"something.else\", \"cue\": \"hi\"}")]
    [InlineData("{\"schema\": 1}")]
    public void RejectsWhatIsNotASidecar(string json) => Assert.Null(ClipSidecar.Parse(json));

    [Fact]
    public void IgnoresFieldsOfTheWrongType()
    {
        var s = ClipSidecar.Parse("""
            {"schema": "quipclipper.clip-meta", "version": "1", "cue": 5, "created": "yesterday",
             "line": {"start": "x"}, "clip": [1, 2], "source": "a string",
             "jellyfin": {"id": "not-a-guid", "season": 1.5, "show": "Show", "year": 1e40},
             "parsed": null}
            """)!;
        Assert.Equal(0, s.Version);
        Assert.Null(s.Cue);
        Assert.Null(s.Created);
        Assert.Null(s.Line);
        Assert.Null(s.Clip);
        Assert.Null(s.SourceName);
        Assert.Null(s.Jellyfin!.Id);
        Assert.Null(s.Jellyfin.Season);
        Assert.Null(s.Jellyfin.Year);
        Assert.Equal("Show", s.Jellyfin.Show);
        Assert.Null(s.Parsed);
    }

    [Fact]
    public void AnEmptyJellyfinBlockIsNone()
    {
        var s = ClipSidecar.Parse("""{"schema": "quipclipper.clip-meta", "jellyfin": {"id": null, "type": "Episode"}}""")!;
        Assert.Null(s.Jellyfin);
    }

    [Fact]
    public void ReadsFromDiskAndNeverThrows()
    {
        using var dir = new TempDir();
        var clip = dir.File("a_00-00-01_Hello.mp4", "x");
        Assert.Null(ClipSidecar.Read(ClipSidecar.PathFor(clip)));

        File.WriteAllText(ClipSidecar.PathFor(clip), Fixture("movie-clip.qc.json"));
        Assert.Equal("Winter sure is long, isn't it?", ClipSidecar.Read(ClipSidecar.PathFor(clip))!.Cue);

        File.WriteAllText(ClipSidecar.PathFor(clip), "{ truncated");
        Assert.Null(ClipSidecar.Read(ClipSidecar.PathFor(clip)));

        File.WriteAllText(ClipSidecar.PathFor(clip), new string(' ', (int)ClipSidecar.MaxBytes + 1));
        Assert.Null(ClipSidecar.Read(ClipSidecar.PathFor(clip)));

        // A directory where the sidecar should be.
        var other = dir.File("b.mp4", "x");
        Directory.CreateDirectory(ClipSidecar.PathFor(other));
        Assert.Null(ClipSidecar.Read(ClipSidecar.PathFor(other)));
    }

    [Fact]
    public void NamesTheSidecarAsQuipclipperDoes()
    {
        Assert.Equal(
            Path.Combine("/clips/Show", ".00-00-46_No_it.mp4.qc.json"),
            ClipSidecar.PathFor("/clips/Show/00-00-46_No_it.mp4"));
        Assert.True(ClipSidecar.IsSidecarName(".a.mp4.qc.json"));
        Assert.False(ClipSidecar.IsSidecarName("a.mp4.qc.json"));
        Assert.False(ClipSidecar.IsSidecarName(".qc.json"));
        Assert.False(ClipSidecar.IsSidecarName("._a.mp4"));
    }
}
