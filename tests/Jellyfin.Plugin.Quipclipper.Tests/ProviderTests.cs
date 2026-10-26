using Jellyfin.Plugin.Quipclipper.Clips;
using MediaBrowser.Controller.Entities;
using MediaBrowser.Controller.Entities.Movies;
using MediaBrowser.Controller.Entities.TV;
using MediaBrowser.Controller.Library;
using MediaBrowser.Controller.Providers;
using MediaBrowser.Model.Drawing;
using MediaBrowser.Model.Entities;
using MediaBrowser.Model.IO;
using Microsoft.Extensions.Logging.Abstractions;
using Moq;
using static Jellyfin.Plugin.Quipclipper.Tests.ClipSidecarTests;

namespace Jellyfin.Plugin.Quipclipper.Tests;

public class ClipMetadataProviderTests
{
    private static readonly ClipMetadataProvider Provider = new(NullLogger<ClipMetadataProvider>.Instance);
    private static readonly DiskDirectoryService Disk = new();

    private static ItemInfo InfoFor(string path) => new(new Video { Path = path });

    [Fact]
    public async Task NamesAClipFromItsSidecar()
    {
        using var dir = new TempDir();
        var clip = dir.File("Crank.Yankers.S01E01/00-00-46_No_it_doesn't_really_matter_Crank_Yankers.mp4", "x");
        File.WriteAllText(ClipSidecar.PathFor(clip), Fixture("episode-backfill.qc.json"));

        var result = await Provider.GetMetadata(InfoFor(clip), Disk, CancellationToken.None);

        Assert.True(result.HasMetadata);
        Assert.Equal("No it doesn't really matter", result.Item.Name);
        Assert.StartsWith("Crank Yankers · S01E01", result.Item.Overview);
        Assert.Equal(["Crank Yankers"], result.Item.Tags);
        Assert.Equal(2002, result.Item.ProductionYear);
        Assert.NotNull(result.Item.PremiereDate);
    }

    [Theory]
    [InlineData(null)]
    [InlineData("{ not json")]
    [InlineData("{\"schema\": \"other\"}")]
    public async Task WithoutAUsableSidecarLeavesTheDefaults(string? sidecar)
    {
        using var dir = new TempDir();
        var clip = dir.File("a.mp4", "x");
        if (sidecar is not null)
        {
            File.WriteAllText(ClipSidecar.PathFor(clip), sidecar);
        }

        var result = await Provider.GetMetadata(InfoFor(clip), Disk, CancellationToken.None);
        Assert.False(result.HasMetadata);
    }

    [Fact]
    public async Task AFailingDirectoryServiceNeverEscapes()
    {
        var broken = new Mock<IDirectoryService>();
        broken.Setup(d => d.GetFile(It.IsAny<string>())).Throws(new IOException("disk gone"));
        var result = await Provider.GetMetadata(InfoFor("/clips/a.mp4"), broken.Object, CancellationToken.None);
        Assert.False(result.HasMetadata);
        Assert.False(Provider.HasChanged(new Video { Path = "/clips/a.mp4" }, broken.Object));
    }

    [Fact]
    public async Task LeavesMoviesAndEpisodesAlone()
    {
        using var dir = new TempDir();
        var clip = dir.File("a.mkv", "x");
        File.WriteAllText(ClipSidecar.PathFor(clip), Fixture("movie-clip.qc.json"));
        var result = await Provider.GetMetadata(new ItemInfo(new Movie { Path = clip }), Disk, CancellationToken.None);
        Assert.False(result.HasMetadata);
        Assert.False(Provider.HasChanged(new Movie { Path = clip, DateLastSaved = DateTime.MinValue }, Disk));
    }

    [Fact]
    public void NoticesAChangedSidecar()
    {
        using var dir = new TempDir();
        var clip = dir.File("a.mp4", "x");
        var video = new Video { Path = clip, DateLastSaved = DateTime.UtcNow.AddMinutes(-5) };
        Assert.False(Provider.HasChanged(video, Disk));

        File.WriteAllText(ClipSidecar.PathFor(clip), Fixture("movie-clip.qc.json"));
        Assert.True(Provider.HasChanged(video, Disk));

        video.DateLastSaved = DateTime.UtcNow.AddMinutes(5);
        Assert.False(Provider.HasChanged(video, Disk));
    }
}

public class ClipFolderMetadataProviderTests
{
    private static readonly ClipFolderMetadataProvider Provider = new(NullLogger<ClipFolderMetadataProvider>.Instance);
    private static readonly DiskDirectoryService Disk = new();

    [Fact]
    public async Task NamesAPerSourceFolder()
    {
        using var dir = new TempDir();
        var clip = dir.File("Bambi.1942.1080p/00-40-20_Winter.mp4", "x");
        File.WriteAllText(ClipSidecar.PathFor(clip), Fixture("movie-clip.qc.json"));
        // An orphaned sidecar (its clip was moved away) doesn't count.
        dir.File("Bambi.1942.1080p/.gone.mp4.qc.json", Fixture("episode-backfill.qc.json"));
        var folder = Path.GetDirectoryName(clip)!;

        var result = await Provider.GetMetadata(new ItemInfo(new Folder { Path = folder }), Disk, CancellationToken.None);

        Assert.True(result.HasMetadata);
        Assert.Equal("Bambi (1942)", result.Item.Name);
        Assert.Equal(["Bambi"], result.Item.Tags);
        Assert.True(Provider.HasChanged(new Folder { Path = folder, DateLastSaved = DateTime.UtcNow.AddHours(-1) }, Disk));
    }

    [Fact]
    public async Task LeavesAMixedFolderAndLibraryRootsAlone()
    {
        using var dir = new TempDir();
        var a = dir.File("LeadStinger/a.mp4", "x");
        var b = dir.File("LeadStinger/b.mp4", "x");
        File.WriteAllText(ClipSidecar.PathFor(a), Fixture("movie-clip.qc.json"));
        File.WriteAllText(ClipSidecar.PathFor(b), Fixture("episode-backfill.qc.json"));
        var folder = Path.GetDirectoryName(a)!;

        Assert.False((await Provider.GetMetadata(new ItemInfo(new Folder { Path = folder }), Disk, CancellationToken.None)).HasMetadata);
        Assert.False((await Provider.GetMetadata(new ItemInfo(new CollectionFolder { Path = folder }), Disk, CancellationToken.None)).HasMetadata);
        Assert.False((await Provider.GetMetadata(new ItemInfo(new Series { Path = folder }), Disk, CancellationToken.None)).HasMetadata);
    }
}

public class ClipImageProviderTests
{
    [Fact]
    public async Task HandsOverACopyOfTheEpisodeStillNeverItsPath()
    {
        using var dir = new TempDir();
        var still = dir.File("metadata/episode/primary.jpg", "jpeg bytes");
        var clip = dir.File("clips/a.mp4", "x");
        File.WriteAllText(ClipSidecar.PathFor(clip), Fixture("episode-backfill.qc.json"));
        var episodeId = Guid.Parse("efddad91d03484dcaf5d941d31f6ff3a");
        var episode = new Episode { Id = episodeId };
        episode.WithImage(ImageType.Primary, still);

        var provider = ProviderWith((episodeId, episode));
        var video = new Video { Path = clip };
        Assert.True(provider.Supports(video));
        Assert.Equal([ImageType.Primary], provider.GetSupportedImages(video));

        var response = await provider.GetImage(video, ImageType.Primary, CancellationToken.None);

        Assert.True(response.HasImage);
        // Jellyfin deletes the file behind response.Path after copying it: it must stay empty.
        Assert.True(string.IsNullOrEmpty(response.Path));
        Assert.Equal(ImageFormat.Jpg, response.Format);
        using (var reader = new StreamReader(response.Stream))
        {
            Assert.Equal("jpeg bytes", await reader.ReadToEndAsync());
        }

        Assert.True(File.Exists(still));
    }

    [Fact]
    public async Task FallsBackToTheSeriesArtworkThenToNothing()
    {
        using var dir = new TempDir();
        var thumb = dir.File("metadata/series/thumb.png", "png");
        var clip = dir.File("clips/a.mp4", "x");
        File.WriteAllText(ClipSidecar.PathFor(clip), Fixture("episode-backfill.qc.json"));
        var episodeId = Guid.Parse("efddad91d03484dcaf5d941d31f6ff3a");
        var series = new Series { Id = Guid.NewGuid() };
        series.WithImage(ImageType.Thumb, thumb);
        var episode = new Episode { Id = episodeId, SeriesId = series.Id };
        episode.WithImage(ImageType.Primary, Path.Combine(dir.Path, "missing.jpg"));

        var response = await ProviderWith((episodeId, episode), (series.Id, series)).GetImage(new Video { Path = clip }, ImageType.Primary, CancellationToken.None);
        Assert.True(response.HasImage);
        Assert.Equal(ImageFormat.Png, response.Format);
        response.Stream.Dispose();

        var none = await ProviderWith().GetImage(new Video { Path = clip }, ImageType.Primary, CancellationToken.None);
        Assert.False(none.HasImage);
    }

    [Fact]
    public async Task AFolderGetsItsSeriesPoster()
    {
        using var dir = new TempDir();
        var poster = dir.File("metadata/series/poster.webp", "webp");
        var clip = dir.File("clips/Crank.Yankers.S01E01/a.mp4", "x");
        File.WriteAllText(ClipSidecar.PathFor(clip), Fixture("episode-backfill.qc.json"));
        var episodeId = Guid.Parse("efddad91d03484dcaf5d941d31f6ff3a");
        var series = new Series { Id = Guid.NewGuid() };
        series.WithImage(ImageType.Primary, poster);
        var episode = new Episode { Id = episodeId, SeriesId = series.Id };

        var folder = new Folder { Path = Path.GetDirectoryName(clip)! };
        var response = await ProviderWith((episodeId, episode), (series.Id, series)).GetImage(folder, ImageType.Primary, CancellationToken.None);
        Assert.True(response.HasImage);
        Assert.Equal(ImageFormat.Webp, response.Format);
        response.Stream.Dispose();
    }

    [Fact]
    public async Task IgnoresOtherItemsAndTypesAndNeverThrows()
    {
        var provider = ProviderWith();
        Assert.False(provider.Supports(new Movie { Path = "/movies/a.mkv" }));
        Assert.False(provider.Supports(new Episode { Path = "/shows/a.mkv" }));
        Assert.False(provider.Supports(new CollectionFolder { Path = "/clips" }));
        Assert.False((await provider.GetImage(new Video { Path = "/clips/a.mp4" }, ImageType.Backdrop, CancellationToken.None)).HasImage);
        Assert.False((await provider.GetImage(new Video { Path = "/nowhere/a.mp4" }, ImageType.Primary, CancellationToken.None)).HasImage);

        var throwing = new Mock<ILibraryManager>();
        throwing.Setup(l => l.GetItemById(It.IsAny<Guid>())).Throws(new InvalidOperationException("db locked"));
        using var dir = new TempDir();
        var clip = dir.File("a.mp4", "x");
        File.WriteAllText(ClipSidecar.PathFor(clip), Fixture("episode-backfill.qc.json"));
        var p = new ClipImageProvider(throwing.Object, Mock.Of<IFileSystem>(), NullLogger<ClipImageProvider>.Instance);
        Assert.False((await p.GetImage(new Video { Path = clip }, ImageType.Primary, CancellationToken.None)).HasImage);
    }

    [Theory]
    [InlineData("a.JPG", ImageFormat.Jpg)]
    [InlineData("a.jpeg", ImageFormat.Jpg)]
    [InlineData("a.png", ImageFormat.Png)]
    [InlineData("a.svg", null)]
    [InlineData("a", null)]
    public void KnowsImageFormats(string path, ImageFormat? expected) => Assert.Equal(expected, ClipImageProvider.ImageFormatOf(path));

    private static ClipImageProvider ProviderWith(params (Guid Id, BaseItem Item)[] items)
    {
        var library = new Mock<ILibraryManager>();
        foreach (var (id, item) in items)
        {
            library.Setup(l => l.GetItemById(id)).Returns(item);
        }

        // The provider reads sidecars through Jellyfin's DirectoryService over this file system.
        var fs = new Mock<IFileSystem>();
        fs.Setup(f => f.GetFileInfo(It.IsAny<string>())).Returns((string p) => DiskDirectoryService.Meta(p)!);
        fs.Setup(f => f.GetFileSystemInfo(It.IsAny<string>())).Returns((string p) => DiskDirectoryService.Meta(p)!);
        fs.Setup(f => f.GetFileSystemEntries(It.IsAny<string>(), It.IsAny<bool>()))
            .Returns((string p, bool _) => Directory.Exists(p) ? new DiskDirectoryService().GetFileSystemEntries(p) : []);
        return new ClipImageProvider(library.Object, fs.Object, NullLogger<ClipImageProvider>.Instance);
    }
}
