# Jellyfin 13 and this plugin

*Checked 2026-10-26 against jellyfin `master` at `ca6dcf73d65e` (which declares 13.0.0),
`release-12.z` at `06de749d435e`, and jellyfin-web `master` at `7f34f027be79`. Read from
the source; no 13.x server was run.*

## Short version

- **There is no Jellyfin 13 yet:** no 13.x tag, pre-release, or `Jellyfin.Controller` /
  `Jellyfin.Model` package (the newest is 12.3.0). So there is no 13.x build of this plugin
  to make yet.
- **Today's `master` would load the 0.x plugin as it is, and every feature would work.**
  Jellyfin loads a plugin when the server's version is at least its `targetAbi`
  (13.0.0 ≥ 12.0.0.0); `master` still targets .NET 10; and no type or member the plugin
  uses has changed or been marked obsolete since 12.0.
- **That can still change before 13.0.** The `v13.0` milestone has dozens of open pull
  requests, and Jellyfin 12.0 itself did not load plugins built for 10.11. The guard
  described in the README turns off just the feature hit by a removed member, but a change
  to an interface the plugin *implements* (a provider interface) stops Jellyfin loading
  the plugin at all; only a new build fixes that.

## What the plugin uses, and whether `master` changes it

| Area | What the plugin uses | On `master` |
|---|---|---|
| Loading | `targetAbi` check (`>=`), the per-plugin load context, `IPluginServiceRegistrator`, hosted services, plugin API controllers, the `RequiresElevation` policy | unchanged |
| Plugin base | `BasePlugin<T>`, `IHasWebPages`, `PluginPageInfo`, `BasePluginConfiguration`, `IApplicationPaths.PluginConfigurationsPath`, `IServerApplicationHost.FriendlyName` | unchanged |
| Clip metadata | `ILocalMetadataProvider<T>`, `MetadataResult<T>`, `ItemInfo`, `IHasItemChangeMonitor`, `IDirectoryService`, `DirectoryService(IFileSystem)` | unchanged (but see #17813 below) |
| Clip artwork | `IDynamicImageProvider`, `DynamicImageResponse`, `IHasOrder`, `ItemImageInfo`, `ImageType`, `ImageFormat` | unchanged |
| Subtitle re-index | `ILibraryManager.ItemAdded`/`ItemUpdated`, `GetVirtualFolders()`, `VirtualFolderInfo` | unchanged |
| Machine transcripts | `ISubtitleProvider`, `SubtitleSearchRequest`, `SubtitleResponse`, `RemoteSubtitleInfo`, `ILibraryManager.FindByPath`, `BaseItem.GetInternalMetadataPath()` | unchanged |
| The saved file's name | `SubtitleManager` names a pick `<stem>.<language lowercased>[.forced][.sdh].<format>` (`.<n>.` when taken); manual searches pass `isAutomated: false` | unchanged, so `en.whisper` still saves `<stem>.en.whisper.srt` |
| Settings page | `ApiClient`, `Dashboard`, `emby-input`/`emby-button`/`emby-checkbox`, an inline script on a `pluginConfigurationPage` | unchanged; jellyfin-web dropped its own use of the jQuery global, which the page doesn't use |

## Open pull requests to watch

- **jellyfin#17813** (shared `DirectoryService` cache): `new DirectoryService(fs)` would share
  one cache that expires a minute after last use. The artwork provider reads a clip's
  sidecar through one, so an edited sidecar could be read stale. If it lands: read the
  sidecar off disk directly there.
- **jellyfin#16750** (server configuration through .NET's options): marks
  `IConfigurationManager` obsolete. The plugin doesn't use it; re-check `BaseItem` and
  `Folder` if it lands.
- **jellyfin-web#8700** (stop using the global `ApiClient` internally): the settings page
  calls `window.ApiClient`. If a later change removes that global, the page breaks even
  though the plugin loads.

## When 13.0 release candidates appear

1. Build the plugin against the `13.0.0-rc` packages (`-p:JellyfinVersion=13.0.0-rc1`): a
   compile is the real check.
2. Keep `targetAbi` at 12.0.0.0 unless something breaks, so 12.x servers keep getting
   updates; if it breaks, release a 13 build with `targetAbi` 13.0.0.0 alongside.
3. On the release candidate: open the settings page, edit a clip's sidecar and rescan,
   and save a machine transcript from the subtitle search.
