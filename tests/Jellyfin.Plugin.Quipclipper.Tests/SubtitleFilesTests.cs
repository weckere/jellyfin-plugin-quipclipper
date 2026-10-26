using Jellyfin.Plugin.Quipclipper.Subtitles;

namespace Jellyfin.Plugin.Quipclipper.Tests;

public class SubtitleFilesTests
{
    [Fact]
    public void A_media_file_owns_the_subtitles_named_like_it()
    {
        using var dir = new TempDir();
        var ep = dir.File("Show/Show.S01E01.mkv", string.Empty);
        dir.File("Show/Show.S01E01.en.srt", "1");
        dir.File("Show/Show.S01E01.en.forced.ass", "1");
        dir.File("Show/Show.S01E01.nfo", "x");          // not a subtitle
        dir.File("Show/Show.S01E02.en.srt", "1");       // another episode's (no media file: owned by nobody)
        dir.File("Show/Show.S01E011.en.srt", "1");      // a longer name is not a sidecar of S01E01

        var found = SubtitleFiles.For(ep);
        Assert.Equal(["Show.S01E01.en.forced.ass", "Show.S01E01.en.srt"], found.Keys.Order(StringComparer.Ordinal));
        Assert.All(found.Values, t => Assert.True(t > 0));
    }

    [Fact]
    public void The_longest_media_name_owns_a_subtitle_two_could_claim()
    {
        using var dir = new TempDir();
        var plain = dir.File("M/Film.mkv", string.Empty);
        var cut = dir.File("M/Film.extended.mkv", string.Empty);
        dir.File("M/Film.extended.en.srt", "1");
        dir.File("M/Film.en.srt", "1");

        var all = SubtitleFiles.InFolder(Path.Combine(dir.Path, "M"));
        Assert.Equal(["Film.en.srt"], all[plain].Keys);
        Assert.Equal(["Film.extended.en.srt"], all[cut].Keys);
    }

    [Fact]
    public void A_folder_that_cant_be_read_has_none()
    {
        Assert.Empty(SubtitleFiles.For("/no/such/folder/x.mkv"));
        Assert.Empty(SubtitleFiles.InFolder("/no/such/folder"));
    }

    [Fact]
    public void Folders_walks_the_tree_and_skips_hidden_ones()
    {
        using var dir = new TempDir();
        dir.File("a/b/x.mkv", string.Empty);
        dir.File("a/.hidden/y.mkv", string.Empty);
        dir.File("c/z.mkv", string.Empty);
        var names = SubtitleFiles.Folders(dir.Path).Select(p => Path.GetRelativePath(dir.Path, p)).ToList();
        Assert.Equal([".", "a", "a/b", "c"], names);
    }

    [Theory]
    [InlineData("x.mkv", true)]
    [InlineData("x.M4B", true)]
    [InlineData("x.iso", false)]
    [InlineData("x.srt", false)]
    [InlineData("", false)]
    public void IsMedia_knows_quipclippers_media(string path, bool expected) => Assert.Equal(expected, SubtitleFiles.IsMedia(path));

    [Fact]
    public void Differ_sees_added_removed_and_rewritten_files()
    {
        var a = new Dictionary<string, long> { ["e.en.srt"] = 1 };
        Assert.False(SubtitleFiles.Differ(a, new Dictionary<string, long> { ["e.en.srt"] = 1 }));
        Assert.True(SubtitleFiles.Differ(a, new Dictionary<string, long> { ["e.en.srt"] = 2 }));
        Assert.True(SubtitleFiles.Differ(a, new Dictionary<string, long> { ["e.en.srt"] = 1, ["e.de.srt"] = 1 }));
        Assert.True(SubtitleFiles.Differ(a, null));
        Assert.False(SubtitleFiles.Differ(null, new Dictionary<string, long>()));
    }
}
