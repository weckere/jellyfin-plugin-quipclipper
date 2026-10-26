using MediaBrowser.Controller.Providers;
using MediaBrowser.Model.IO;

namespace Jellyfin.Plugin.Quipclipper.Tests;

/// <summary>A temporary folder, deleted afterwards.</summary>
internal sealed class TempDir : IDisposable
{
    public TempDir()
    {
        Path = System.IO.Path.Combine(System.IO.Path.GetTempPath(), "qc-plugin-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(Path);
    }

    public string Path { get; }

    public string File(string relative, string content)
    {
        var full = System.IO.Path.Combine(Path, relative);
        Directory.CreateDirectory(System.IO.Path.GetDirectoryName(full)!);
        System.IO.File.WriteAllText(full, content);
        return full;
    }

    public void Dispose()
    {
        try
        {
            Directory.Delete(Path, recursive: true);
        }
        catch (IOException)
        {
        }
    }
}

/// <summary>Jellyfin's directory cache over the real disk, uncached (its own needs IFileSystem).</summary>
internal sealed class DiskDirectoryService : IDirectoryService
{
    public static FileSystemMetadata? Meta(string path)
    {
        FileSystemInfo info = Directory.Exists(path) ? new DirectoryInfo(path) : new FileInfo(path);
        if (!info.Exists)
        {
            return new FileSystemMetadata { FullName = path, Name = System.IO.Path.GetFileName(path), Exists = false };
        }

        return new FileSystemMetadata
        {
            FullName = info.FullName,
            Name = info.Name,
            Exists = true,
            IsDirectory = info is DirectoryInfo,
            LastWriteTimeUtc = info.LastWriteTimeUtc,
            Length = info is FileInfo f ? f.Length : 0,
            Extension = info.Extension,
        };
    }

    public FileSystemMetadata[] GetFileSystemEntries(string path)
        => Directory.EnumerateFileSystemEntries(path).Select(p => Meta(p)!).ToArray();

    public List<FileSystemMetadata> GetDirectories(string path)
        => GetFileSystemEntries(path).Where(e => e.IsDirectory).ToList();

    public List<FileSystemMetadata> GetFiles(string path)
        => GetFileSystemEntries(path).Where(e => !e.IsDirectory).ToList();

    public FileSystemMetadata? GetFile(string path) => Meta(path) is { IsDirectory: false } m ? m : null;

    public FileSystemMetadata? GetDirectory(string path) => Meta(path) is { IsDirectory: true } m ? m : null;

    public FileSystemMetadata? GetFileSystemEntry(string path) => Meta(path);

    public IReadOnlyList<string> GetFilePaths(string path) => GetFiles(path).Select(f => f.FullName).ToList();

    public IReadOnlyList<string> GetFilePaths(string path, bool clearCache) => GetFilePaths(path);

    public void Invalidate(string path)
    {
    }

    public void Move(string source, string destination) => throw new NotSupportedException();

    public bool IsAccessible(string path) => true;
}

/// <summary>Jellyfin's items reach static services (set by the server at start-up); the tests set the one they touch.</summary>
internal static class JellyfinStatics
{
    [System.Runtime.CompilerServices.ModuleInitializer]
    internal static void Init()
    {
        var sources = new Moq.Mock<MediaBrowser.Controller.Library.IMediaSourceManager>();
        sources.Setup(m => m.GetPathProtocol(Moq.It.IsAny<string>())).Returns(MediaBrowser.Model.MediaInfo.MediaProtocol.File);
        MediaBrowser.Controller.Entities.BaseItem.MediaSourceManager = sources.Object;
    }

    /// <summary>Gives an item one image of a type, as the server's database would.</summary>
    public static void WithImage(this MediaBrowser.Controller.Entities.BaseItem item, MediaBrowser.Model.Entities.ImageType type, string path)
        => item.ImageInfos = [.. item.ImageInfos, new MediaBrowser.Controller.Entities.ItemImageInfo { Path = path, Type = type }];
}
