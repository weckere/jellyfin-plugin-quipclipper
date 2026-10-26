using Jellyfin.Plugin.Quipclipper.Clips;
using static Jellyfin.Plugin.Quipclipper.Tests.ClipSidecarTests;

namespace Jellyfin.Plugin.Quipclipper.Tests;

public class ClipDetailsTests
{
    private static ClipDetails Details(string fixture, string stem = "stem")
        => ClipDetails.From(ClipSidecar.Parse(Fixture(fixture))!, stem);

    [Fact]
    public void AnEpisodeClipShowsItsLineShowAndEpisode()
    {
        var d = Details("episode-backfill.qc.json");
        Assert.Equal("No it doesn't really matter", d.Name);
        Assert.Equal("Crank Yankers · S01E01 · Tenacious D & Wanda Sykes · at 0:46 · cut 2026-06-21", d.Overview);
        Assert.Equal(["Crank Yankers"], d.Tags);
        Assert.Equal(2002, d.ProductionYear);
        Assert.Equal(new DateTime(2026, 6, 21, 9, 33, 0, DateTimeKind.Utc).Date, d.PremiereDate!.Value.Date);
        Assert.Equal(DateTimeKind.Utc, d.PremiereDate.Value.Kind);
        Assert.Equal(Guid.Parse("efddad91d03484dcaf5d941d31f6ff3a"), d.SourceId);
    }

    [Fact]
    public void AMovieClipShowsTitleAndYearAndTheLineNotThePadding()
    {
        var d = Details("movie-clip.qc.json");
        Assert.Equal("Winter sure is long, isn't it?", d.Name);
        Assert.Equal("Bambi (1942) · at 40:19 · cut 2026-10-24", d.Overview);
        Assert.Equal(["Bambi"], d.Tags);
        Assert.Equal(1942, d.ProductionYear);
    }

    [Fact]
    public void ALongSourceGetsHours()
    {
        var d = Details("leadstinger-quip.qc.json");
        Assert.Equal("All women are detectives. Send me a picture of the license plate.", d.Name);
        Assert.StartsWith("Nobody Wants This · S03E08 · Watch What Happens · at 7:38 · cut ", d.Overview);
        Assert.Equal("1:07:52", ClipDetails.Timestamp(4072.9));
        Assert.Equal("0:00", ClipDetails.Timestamp(-3));
    }

    [Fact]
    public void WithoutCueOrJellyfinItFallsBackToTheNamesQuipclipperRead()
    {
        var d = Details("no-cue-backfill.qc.json", "S03E26_The_Best_of_Both_Worlds_1_Captain's_log_stardate_439891");
        Assert.Equal("S03E26 The Best of Both Worlds 1 Captain's log stardate 439891", d.Name);
        Assert.Equal("Star Trek The Next Generation · S03E26 · cut 2026-10-21", d.Overview);
        Assert.Empty(d.Tags);
        Assert.Null(d.ProductionYear);
        Assert.Null(d.SourceId);
    }

    [Fact]
    public void APodcastClipKeepsItsSourceName()
    {
        var d = Details("podcast-clip.qc.json", "00-10-00_Self-Hosted_SRE");
        Assert.Equal("00-10-00 Self-Hosted SRE", d.Name);
        Assert.Equal("Self-Hosted SRE · at 10:00 · cut 2026-10-21", d.Overview);
        Assert.Empty(d.Tags);
    }

    [Fact]
    public void ASetIsNamedAfterItself()
        => Assert.Equal("101 Dalmatians (1961)", Details("set.qc.json").Name);

    [Fact]
    public void ABareSidecarStillWorks()
    {
        var d = ClipDetails.From(ClipSidecar.Parse("""{"schema": "quipclipper.clip-meta"}""")!, "a_b");
        Assert.Equal("a b", d.Name);
        Assert.Null(d.Overview);
        Assert.Empty(d.Tags);
        Assert.Null(d.PremiereDate);
    }

    [Theory]
    [InlineData("- THANK YOU FOR CALLING RICK'S TALLY HOE", "THANK YOU FOR CALLING RICK'S TALLY HOE")]
    [InlineData("  Two\nlines  ", "Two lines")]
    [InlineData("—Interrupted", "Interrupted")]
    [InlineData("-", null)]
    [InlineData("   ", null)]
    [InlineData(null, null)]
    [InlineData("Peanut. Plain.", "Peanut. Plain.")]
    public void CleansALine(string? cue, string? expected) => Assert.Equal(expected, ClipDetails.CleanLine(cue));

    [Fact]
    public void LabelsSources()
    {
        Assert.Equal("Bambi", ClipDetails.SourceLabel(new JellyfinSource(null, "Movie", "Bambi", null, null, null, null)));
        Assert.Equal("Show · S02E03", ClipDetails.SourceLabel(new JellyfinSource(null, "Episode", "Show", 2, 3, "Show", 2001)));
        Assert.Null(ClipDetails.SourceLabel(new JellyfinSource(null, "Episode", null, null, null, null, null)));
        Assert.Null(ClipDetails.SourceLabel(null));
    }
}

public class FolderDetailsTests
{
    private static readonly Guid A = Guid.NewGuid();
    private static readonly Guid B = Guid.NewGuid();

    private static ClipSidecar Clip(Guid? id, string type, string show, int? s = null, int? e = null, string? title = null, int? year = null)
        => new() { Jellyfin = new JellyfinSource(id, type, show, s, e, title, year) };

    [Fact]
    public void OneSourceNamesTheFolder()
    {
        var d = FolderDetails.From([Clip(A, "Episode", "Crank Yankers", 1, 1, "Tenacious D & Wanda Sykes", 2002), Clip(A, "Episode", "Crank Yankers", 1, 1), new ClipSidecar()])!;
        Assert.Equal("Crank Yankers · S01E01 · Tenacious D & Wanda Sykes", d.Name);
        Assert.Equal(["Crank Yankers"], d.Tags);
        Assert.Equal(2002, d.ProductionYear);
        Assert.Equal(A, d.SourceId);
    }

    [Fact]
    public void OneMovieNamesTheFolder()
        => Assert.Equal("Bambi (1942)", FolderDetails.From([Clip(A, "Movie", "Bambi", title: "Bambi", year: 1942)])!.Name);

    [Fact]
    public void EpisodesOfOneShowNameItAfterTheShow()
    {
        var d = FolderDetails.From([Clip(A, "Episode", "Star Trek: The Next Generation", 3, 26), Clip(B, "Episode", "star trek: the next generation", 5, 23)])!;
        Assert.Equal("Star Trek: The Next Generation", d.Name);
        Assert.Null(d.ProductionYear);
    }

    [Fact]
    public void MixedSourcesKeepTheFolderName()
    {
        Assert.Null(FolderDetails.From([Clip(A, "Movie", "Bambi"), Clip(B, "Episode", "Crank Yankers", 1, 2)]));
        Assert.Null(FolderDetails.From([Clip(A, "Movie", "Bambi"), Clip(B, "Movie", "Bambi")]));
        Assert.Null(FolderDetails.From([new ClipSidecar(), Clip(null, "Movie", "Bambi")]));
        Assert.Null(FolderDetails.From([]));
    }
}
