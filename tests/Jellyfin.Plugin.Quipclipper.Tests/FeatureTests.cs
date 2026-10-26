using Jellyfin.Plugin.Quipclipper.Clips;
using Jellyfin.Plugin.Quipclipper.Subtitles;
using Jellyfin.Plugin.Quipclipper.Transcripts;
using MediaBrowser.Controller.Entities;
using MediaBrowser.Controller.Library;
using MediaBrowser.Controller.Providers;
using MediaBrowser.Controller.Subtitles;
using MediaBrowser.Model.Entities;
using MediaBrowser.Model.IO;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;
using Moq;
using static Jellyfin.Plugin.Quipclipper.Tests.ClipSidecarTests;

namespace Jellyfin.Plugin.Quipclipper.Tests;

/// <summary>A logger that keeps what was logged.</summary>
internal sealed class ListLogger<T> : ILogger<T>
{
    public List<(LogLevel Level, string Message, Exception? Exception)> Entries { get; } = [];

    public IDisposable? BeginScope<TState>(TState state)
        where TState : notnull => null;

    public bool IsEnabled(LogLevel logLevel) => true;

    public void Log<TState>(LogLevel logLevel, EventId eventId, TState state, Exception? exception, Func<TState, Exception?, string> formatter)
    {
        lock (Entries)
        {
            Entries.Add((logLevel, formatter(state, exception), exception));
        }
    }

    public List<string> Errors()
    {
        lock (Entries)
        {
            return Entries.Where(e => e.Level == LogLevel.Error).Select(e => e.Message).ToList();
        }
    }
}

/// <summary>
/// The guard every entry point runs through: a Jellyfin that doesn't match what the plugin
/// was built against (a missing method, a changed type) turns that one feature off, logged
/// once, instead of failing every item of a scan. Each test gives the code under test a
/// feature of its own, so nothing here touches the shared ones.
/// </summary>
public class FeatureTests
{
    private static MissingMethodException Gone() => new("MediaBrowser.Controller.Library.ILibraryManager", "GetItemById");

    [Theory]
    [MemberData(nameof(Mismatches))]
    public void It_knows_a_mismatch_from_an_ordinary_error(Exception exception, bool mismatch)
        => Assert.Equal(mismatch, Feature.IsMismatch(exception));

    public static TheoryData<Exception, bool> Mismatches() => new()
    {
        { new MissingMethodException("BaseItem", "GetImageInfo"), true },
        { new MissingFieldException("x"), true },
        { new TypeLoadException("x"), true },
        { new FileLoadException("x"), true },
        { new FileNotFoundException("gone", "MediaBrowser.Controller, Version=13.0.0.0, Culture=neutral, PublicKeyToken=null"), true },
        { new TypeInitializationException("Sidecars", new MissingMethodException("x")), true },
        { new FileNotFoundException("gone", "/clips/a.mp4.qc.json"), false },
        { new IOException("disk gone"), false },
        { new InvalidOperationException("db locked"), false },
        { new TypeInitializationException("Sidecars", new InvalidOperationException("x")), false },
    };

    [Fact]
    public void A_mismatch_turns_the_feature_off_logged_once_and_the_work_is_not_tried_again()
    {
        var feature = new Feature("test");
        var logger = new ListLogger<FeatureTests>();
        var tries = 0;
        int Work()
        {
            tries++;
            throw Gone();
        }

        Assert.Equal(7, feature.Run(logger, () => 42 - 35, -1));
        Assert.Equal(-1, feature.Run(logger, Work, -1));
        Assert.Equal(-1, feature.Run(logger, Work, -1));
        Assert.Equal(1, tries);
        Assert.False(feature.IsOn);
        Assert.StartsWith("MissingMethodException: ", feature.Reason);
        var error = Assert.Single(logger.Errors());
        Assert.Contains("test is off until Jellyfin restarts", error);
    }

    [Fact]
    public async Task An_ordinary_error_is_the_entry_points_own_and_leaves_the_feature_on()
    {
        var feature = new Feature("test");
        Assert.Throws<IOException>(() => feature.Run<int>(NullLogger.Instance, () => throw new IOException("disk gone"), 0));
        await Assert.ThrowsAsync<IOException>(() => feature.RunAsync<int>(NullLogger.Instance, () => throw new IOException("disk gone"), 0));
        Assert.True(feature.IsOn);
        Assert.Equal(3, await feature.RunAsync(NullLogger.Instance, () => Task.FromResult(3), 0));
        Assert.Equal(0, await feature.RunAsync<int>(NullLogger.Instance, () => throw Gone(), 0));
        Assert.False(feature.IsOn);
    }

    [Fact]
    public void A_part_of_the_registration_that_fails_turns_its_features_off_and_is_logged_when_the_plugin_starts()
    {
        var a = new Feature("a");
        var b = new Feature("b");
        var c = new Feature("c");
        Assert.True(PluginServiceRegistrator.Register([a], () => { }));
        Assert.False(PluginServiceRegistrator.Register([a, b], () => throw new TypeLoadException("ISubtitleProvider changed")));
        Assert.Throws<InvalidOperationException>(() => PluginServiceRegistrator.Register([c], () => throw new InvalidOperationException("a bug")));
        Assert.False(a.IsOn);
        Assert.False(b.IsOn);
        Assert.True(c.IsOn);

        var logger = new ListLogger<Plugin>();
        Feature.LogUnlogged(logger, [a, b, c]);
        Feature.LogUnlogged(logger, [a, b, c]);
        Assert.Equal(2, logger.Errors().Count);
        Assert.Contains("a is off until Jellyfin restarts (TypeLoadException: ISubtitleProvider changed)", logger.Errors()[0]);
    }

    [Fact]
    public void The_shared_features_are_one_per_feature_and_the_providers_use_theirs()
    {
        Assert.Equal(["clip metadata", "clip artwork", "subtitle re-index", "machine transcripts"], Feature.All.Select(f => f.Name));
        Assert.Same(Feature.ClipMetadata, new ClipMetadataProvider(NullLogger<ClipMetadataProvider>.Instance).Feature);
        Assert.Same(Feature.ClipMetadata, new ClipFolderMetadataProvider(NullLogger<ClipFolderMetadataProvider>.Instance).Feature);
        Assert.Same(Feature.ClipArtwork, new ClipImageProvider(Mock.Of<ILibraryManager>(), Mock.Of<IFileSystem>(), NullLogger<ClipImageProvider>.Instance).Feature);
        Assert.Same(Feature.Transcripts, new TranscriptSubtitleProvider(new MemoryConfig(), new FakeQuipclipper().Client, NullLogger<TranscriptSubtitleProvider>.Instance).Feature);
    }

    [Fact]
    public async Task Clip_metadata_goes_off_alone()
    {
        var feature = new Feature("clip metadata");
        var logger = new ListLogger<ClipMetadataProvider>();
        var provider = new ClipMetadataProvider(logger, feature);
        var folders = new ClipFolderMetadataProvider(NullLogger<ClipFolderMetadataProvider>.Instance, feature);
        var gone = new Mock<IDirectoryService>();
        gone.Setup(d => d.GetFile(It.IsAny<string>())).Throws(Gone());

        var result = await provider.GetMetadata(new ItemInfo(new Video { Path = "/clips/a.mp4" }), gone.Object, default);
        Assert.False(result.HasMetadata);
        Assert.False(feature.IsOn);
        Assert.Single(logger.Errors());

        // Off: neither provider asks Jellyfin anything more, for any item.
        Assert.False((await provider.GetMetadata(new ItemInfo(new Video { Path = "/clips/b.mp4" }), gone.Object, default)).HasMetadata);
        Assert.False(provider.HasChanged(new Video { Path = "/clips/b.mp4" }, gone.Object));
        Assert.False((await folders.GetMetadata(new ItemInfo(new Folder { Path = "/clips" }), gone.Object, default)).HasMetadata);
        gone.Verify(d => d.GetFile(It.IsAny<string>()), Times.Once);
        gone.Verify(d => d.GetFiles(It.IsAny<string>()), Times.Never);
        Assert.Single(logger.Errors());

        // The other features carry on: clip artwork is a separate feature.
        Assert.True(Feature.ClipArtwork.IsOn);
    }

    [Fact]
    public async Task A_folders_metadata_and_change_check_are_guarded_too()
    {
        var feature = new Feature("clip metadata");
        var folders = new ClipFolderMetadataProvider(NullLogger<ClipFolderMetadataProvider>.Instance, feature);
        var gone = new Mock<IDirectoryService>();
        gone.Setup(d => d.GetFiles(It.IsAny<string>())).Throws(new TypeLoadException("FileSystemMetadata"));
        Assert.False(folders.HasChanged(new Folder { Path = "/clips/x" }, gone.Object));
        Assert.False(feature.IsOn);
        Assert.False((await folders.GetMetadata(new ItemInfo(new Folder { Path = "/clips/x" }), gone.Object, default)).HasMetadata);
        gone.Verify(d => d.GetFiles(It.IsAny<string>()), Times.Once);
    }

    [Fact]
    public async Task Clip_artwork_goes_off_alone()
    {
        using var dir = new TempDir();
        var clip = dir.File("a.mp4", "x");
        File.WriteAllText(ClipSidecar.PathFor(clip), Fixture("episode-backfill.qc.json"));
        var fs = new Mock<IFileSystem>();
        fs.Setup(f => f.GetFileInfo(It.IsAny<string>())).Returns((string p) => DiskDirectoryService.Meta(p)!);
        fs.Setup(f => f.GetFileSystemInfo(It.IsAny<string>())).Returns((string p) => DiskDirectoryService.Meta(p)!);
        var library = new Mock<ILibraryManager>();
        library.Setup(l => l.GetItemById(It.IsAny<Guid>())).Throws(Gone());
        var feature = new Feature("clip artwork");
        var logger = new ListLogger<ClipImageProvider>();
        var provider = new ClipImageProvider(library.Object, fs.Object, logger, feature);

        Assert.True(provider.Supports(new Video { Path = clip }));
        Assert.False((await provider.GetImage(new Video { Path = clip }, ImageType.Primary, default)).HasImage);
        Assert.False(feature.IsOn);
        Assert.False(provider.Supports(new Video { Path = clip }));
        Assert.False((await provider.GetImage(new Video { Path = clip }, ImageType.Primary, default)).HasImage);
        library.Verify(l => l.GetItemById(It.IsAny<Guid>()), Times.Once);
        Assert.Single(logger.Errors());
        Assert.True(Feature.ClipMetadata.IsOn);
    }

    [Fact]
    public async Task Machine_transcripts_go_off_alone()
    {
        var config = new ThrowingConfig(MemoryConfig.Connected());
        var feature = new Feature("machine transcripts");
        var logger = new ListLogger<TranscriptSubtitleProvider>();
        var provider = new TranscriptSubtitleProvider(config, new FakeQuipclipper().Client, logger, null, feature);
        var request = new SubtitleSearchRequest { MediaPath = "/shows/a.mkv", TwoLetterISOLanguageName = "en", Language = "eng" };

        config.Throw = Gone();
        Assert.Empty(await provider.Search(request, default));
        Assert.False(feature.IsOn);
        config.Throw = null;
        Assert.Empty(await provider.Search(request, default));
        var off = await Assert.ThrowsAsync<InvalidOperationException>(() => provider.GetSubtitles("m.en.x.y", default));
        Assert.Contains("machine transcripts are off until Jellyfin restarts", off.Message);
        Assert.Equal(1, config.Reads);
        Assert.Single(logger.Errors());
    }

    [Fact]
    public async Task The_subtitle_watchers_item_events_go_off_alone()
    {
        using var dir = new TempDir();
        var config = new ThrowingConfig(MemoryConfig.Connected());
        var library = new Mock<ILibraryManager>();
        var watch = new SubtitleWatch(Path.Combine(dir.Path, "state.json"), config, new FakeQuipclipper().Client, () => [], new ManualClock(), NullLogger.Instance);
        var feature = new Feature("subtitle re-index");
        var logger = new ListLogger<SubtitleWatcher>();
        using var watcher = new SubtitleWatcher(library.Object, watch, config, new ManualClock(), logger, feature);
        await watcher.StartAsync(default);

        config.Throw = Gone();
        library.Raise(l => l.ItemUpdated += null, null, new ItemChangeEventArgs { Item = new Video { Path = "/shows/a.mkv" } });
        Assert.False(feature.IsOn);
        config.Throw = null;
        library.Raise(l => l.ItemUpdated += null, null, new ItemChangeEventArgs { Item = new Video { Path = "/shows/b.mkv" } });
        Assert.Equal(1, config.Reads);
        Assert.Single(logger.Errors());
        await watcher.StopAsync(default);
    }

    [Fact]
    public async Task A_jellyfin_whose_item_events_changed_still_starts()
    {
        var library = new Mock<ILibraryManager>();
        library.SetupAdd(l => l.ItemAdded += It.IsAny<EventHandler<ItemChangeEventArgs>>()).Throws(new MissingMethodException("ILibraryManager", "add_ItemAdded"));
        var config = MemoryConfig.Connected();
        using var dir = new TempDir();
        var watch = new SubtitleWatch(Path.Combine(dir.Path, "state.json"), config, new FakeQuipclipper().Client, () => [], new ManualClock(), NullLogger.Instance);
        var feature = new Feature("subtitle re-index");
        using var watcher = new SubtitleWatcher(library.Object, watch, config, new ManualClock(), new ListLogger<SubtitleWatcher>(), feature);

        await watcher.StartAsync(default); // doesn't throw: a hosted service that throws stops Jellyfin starting
        Assert.False(feature.IsOn);
        Assert.Null(watcher.Loop);
        await watcher.StopAsync(default);
    }

    /// <summary>Settings that throw what they are told to (a stand-in for a Jellyfin API that's gone), counting the reads.</summary>
    private sealed class ThrowingConfig(MemoryConfig inner) : Server.IConfigStore
    {
        public event EventHandler? Changed
        {
            add => inner.Changed += value;
            remove => inner.Changed -= value;
        }

        public Exception? Throw { get; set; }

        public int Reads { get; private set; }

        public Configuration.PluginConfiguration Current
        {
            get
            {
                Reads++;
                return Throw is { } ex ? throw ex : inner.Current;
            }
        }

        public void Save(Configuration.PluginConfiguration configuration) => inner.Save(configuration);
    }
}
