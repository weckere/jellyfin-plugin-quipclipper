using System;
using System.Collections.Generic;
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
/// need no registration: Jellyfin finds them by type scan. Each part is registered on its
/// own: one this Jellyfin can't take turns its features off (<see cref="Feature"/>), where
/// an exception out of here would have Jellyfin mark the whole plugin as malfunctioning.
/// </summary>
public class PluginServiceRegistrator : IPluginServiceRegistrator
{
    /// <summary>The watcher's state file, beside the plugin's configuration.</summary>
    public const string StateFileName = "quipclipper-subtitles.json";

    /// <inheritdoc />
    public void RegisterServices(IServiceCollection serviceCollection, IServerApplicationHost applicationHost)
    {
        ArgumentNullException.ThrowIfNull(serviceCollection);
        ArgumentNullException.ThrowIfNull(applicationHost);
        if (Register([Feature.SubtitleReindex, Feature.Transcripts], () => RegisterConnection(serviceCollection, applicationHost)))
        {
            Register([Feature.SubtitleReindex], () => RegisterWatcher(serviceCollection));
            Register([Feature.Transcripts], () => RegisterTranscripts(serviceCollection));
        }
    }

    /// <summary>Runs one part of the registration; a mismatch turns its features off.</summary>
    /// <param name="features">What the part is for.</param>
    /// <param name="register">The part. In its own method, so that a mismatch in it surfaces here.</param>
    /// <returns>True when it registered.</returns>
    internal static bool Register(IReadOnlyList<Feature> features, Action register)
    {
        ArgumentNullException.ThrowIfNull(features);
        ArgumentNullException.ThrowIfNull(register);
        try
        {
            register();
            return true;
        }
        catch (Exception ex) when (Feature.IsMismatch(ex))
        {
            foreach (var feature in features)
            {
                feature.TurnOff(null, ex); // logged when Jellyfin creates the plugin
            }

            return false;
        }
    }

    /// <summary>Jellyfin's own metadata folder for the item at a media path (where it saves subtitles when "Save subtitles with media" is off).</summary>
    /// <param name="library">Jellyfin's library.</param>
    /// <param name="mediaPath">The media file.</param>
    /// <returns>The folder, or null when Jellyfin has no such item.</returns>
    internal static string? MetadataFolder(ILibraryManager library, string mediaPath)
        => library.FindByPath(mediaPath, false)?.GetInternalMetadataPath();

    // Pairing, the settings and the watcher's state: what the settings page needs.
    private static void RegisterConnection(IServiceCollection serviceCollection, IServerApplicationHost applicationHost)
    {
        serviceCollection.AddSingleton<IConfigStore, PluginConfigStore>();
        serviceCollection.AddSingleton<Discovery>();
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
    }

    private static void RegisterWatcher(IServiceCollection serviceCollection)
        => serviceCollection.AddHostedService<SubtitleWatcher>();

    private static void RegisterTranscripts(IServiceCollection serviceCollection)
        => serviceCollection.AddSingleton<ISubtitleProvider>(sp =>
        {
            var factory = sp.GetRequiredService<System.Net.Http.IHttpClientFactory>();
            var library = sp.GetRequiredService<ILibraryManager>();
            return new TranscriptSubtitleProvider(
                sp.GetRequiredService<IConfigStore>(),
                () => new QuipclipperClient(factory.CreateClient(NamedClient.Default), TranscriptSubtitleProvider.Timeout),
                sp.GetRequiredService<ILogger<TranscriptSubtitleProvider>>(),
                path => MetadataFolder(library, path));
        });
}
