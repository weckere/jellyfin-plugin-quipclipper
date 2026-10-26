using System.Text.Json;
using System.Text.RegularExpressions;

namespace Jellyfin.Plugin.Quipclipper.Tests;

public partial class VersionTests
{
    private static string RepoFile(string name)
    {
        var dir = new DirectoryInfo(AppContext.BaseDirectory);
        while (dir is not null && !File.Exists(Path.Combine(dir.FullName, "Directory.Build.props")))
        {
            dir = dir.Parent;
        }

        return File.ReadAllText(Path.Combine(dir!.FullName, name));
    }

    [Fact]
    public void TheVersionIsTheChangelogsLatestRelease()
    {
        // Only scripts/release.sh changes the version, together with CHANGELOG.md.
        var version = VersionProp().Match(RepoFile("Directory.Build.props")).Groups[1].Value;
        var released = ReleaseHeading().Match(RepoFile("CHANGELOG.md"));
        Assert.Equal(released.Success ? released.Groups[1].Value : "0.0.0.0", version);
        Assert.Equal(Version.Parse(version), typeof(Plugin).Assembly.GetName().Version);
    }

    [Fact]
    public void ThePackageMetadataMatchesThePlugin()
    {
        using var meta = JsonDocument.Parse(RepoFile("plugin-meta.json"));
        var root = meta.RootElement;
        Assert.Equal(Plugin.PluginId, root.GetProperty("guid").GetGuid());
        Assert.Equal("quipclipper", root.GetProperty("name").GetString());
        // The floor: a plugin loads on every server at least this version (Jellyfin has no ceiling).
        Assert.Equal("12.0.0.0", root.GetProperty("targetAbi").GetString());
    }

    [GeneratedRegex(@"<Version>([0-9.]+)</Version>")]
    private static partial Regex VersionProp();

    [GeneratedRegex(@"^## \[(\d+\.\d+\.\d+\.\d+)\]", RegexOptions.Multiline)]
    private static partial Regex ReleaseHeading();
}
