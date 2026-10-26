using System;
using System.IO;
using Jellyfin.Plugin.Quipclipper.Server;
using Jellyfin.Plugin.Quipclipper.Subtitles;
using Jellyfin.Plugin.Quipclipper.Transcripts;
using MediaBrowser.Common.Configuration;
using MediaBrowser.Common.Net;
using MediaBrowser.Controller;
using MediaBrowser.Controller.Library;
using MediaBrowser.Controller.Plugins;
using MediaBrowser.Controller.Subtitles;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;
using Microsoft.Extensions.Logging;

namespace Jellyfin.Plugin.Quipclipper;

/// <summary>
/// Registers the subtitle watcher and pairing (Phase 2) and the transcript subtitle provider
/// (Phase 3; Jellyfin takes subtitle providers from DI only). The clip metadata providers
/// need no registration: Jellyfin finds them by type scan.
/// </summary>
public class PluginServiceRegistrator : IPluginServiceRegistrator
{
    /// <summary>The watcher's state file, beside the plugin's configuration.</summary>
    public const string StateFileName = "quipclipper-subtitles.json";

    /// <inheritdoc />
    public void RegisterServices(IServiceCollection serviceCollection, IServerApplicationHost applicationHost)
    {
        ArgumentNullException.ThrowIfNull(applicationHost);
        serviceCollection.AddSingleton<IConfigStore, PluginConfigStore>();
        serviceCollection.TryAddSingleton(TimeProvider.System);
        serviceCollection.AddSingleton<Func<QuipclipperClient>>(sp =>
        {
            var factory = sp.GetRequiredService<System.Net.Http.IHttpClientFactory>();
            return () => new QuipclipperClient(factory.CreateClient(NamedClient.Default));
        });
        serviceCollection.AddSingleton(sp => new PairingService(
            sp.GetRequiredService<IConfigStore>(),
            sp.GetRequiredService<Func<QuipclipperClient>>(),
            () => applicationHost.FriendlyName,
            sp.GetRequiredService<TimeProvider>()));
        serviceCollection.AddSingleton(sp =>
        {
            var paths = sp.GetRequiredService<IApplicationPaths>();
            var library = sp.GetRequiredService<ILibraryManager>();
            return new SubtitleWatch(
                Path.Combine(paths.PluginConfigurationsPath, StateFileName),
                sp.GetRequiredService<IConfigStore>(),
                sp.GetRequiredService<Func<QuipclipperClient>>(),
                () => SubtitleWatcher.Libraries(library),
                sp.GetRequiredService<TimeProvider>(),
                sp.GetRequiredService<ILogger<SubtitleWatch>>());
        });
        serviceCollection.AddHostedService<SubtitleWatcher>();
        serviceCollection.AddSingleton<ISubtitleProvider>(sp =>
        {
            var factory = sp.GetRequiredService<System.Net.Http.IHttpClientFactory>();
            return new TranscriptSubtitleProvider(
                sp.GetRequiredService<IConfigStore>(),
                () => new QuipclipperClient(factory.CreateClient(NamedClient.Default), TranscriptSubtitleProvider.Timeout),
                sp.GetRequiredService<ILogger<TranscriptSubtitleProvider>>());
        });
    }
}
