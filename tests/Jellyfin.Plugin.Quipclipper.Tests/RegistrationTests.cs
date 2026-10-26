using Jellyfin.Plugin.Quipclipper.Api;
using Jellyfin.Plugin.Quipclipper.Server;
using Jellyfin.Plugin.Quipclipper.Subtitles;
using MediaBrowser.Common.Configuration;
using MediaBrowser.Controller;
using MediaBrowser.Controller.Library;
using MediaBrowser.Model.Entities;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;
using Moq;

namespace Jellyfin.Plugin.Quipclipper.Tests;

/// <summary>What Jellyfin's container will build from the registrations: every service resolves.</summary>
public class RegistrationTests
{
    [Fact]
    public void Every_service_and_the_controller_resolve()
    {
        using var dir = new TempDir();
        var services = new ServiceCollection();
        var paths = new Mock<IApplicationPaths>();
        paths.Setup(p => p.PluginConfigurationsPath).Returns(dir.Path);
        var library = new Mock<ILibraryManager>();
        library.Setup(l => l.GetVirtualFolders()).Returns([new VirtualFolderInfo { Name = "Shows", ItemId = "abc", Locations = ["/shows"] }]);
        var host = new Mock<IServerApplicationHost>();
        host.Setup(h => h.FriendlyName).Returns("wizfin");
        services.AddSingleton(paths.Object);
        services.AddSingleton(library.Object);
        services.AddSingleton(typeof(ILogger<>), typeof(NullLogger<>));
        services.AddHttpClient();

        new PluginServiceRegistrator().RegisterServices(services, host.Object);
        services.AddTransient<QuipclipperController>();
        using var provider = services.BuildServiceProvider(new ServiceProviderOptions { ValidateOnBuild = true, ValidateScopes = true });

        Assert.NotNull(provider.GetRequiredService<PairingService>());
        Assert.NotNull(provider.GetRequiredService<QuipclipperController>());
        Assert.IsType<SubtitleWatcher>(Assert.Single(provider.GetServices<IHostedService>()));
        var watch = provider.GetRequiredService<SubtitleWatch>();
        Assert.Equal(["/shows"], watch.WatchedLocations([]));
        Assert.NotNull(provider.GetRequiredService<Func<QuipclipperClient>>()());
    }
}
