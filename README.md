# jellyfin-plugin-quipclipper

A [Jellyfin](https://jellyfin.org) 12.x plugin for [quipclipper](https://github.com/weckere/quipclipper),
the dialogue-search clip cutter.

quipclipper cuts a line out of a movie or episode into its clips folder, and writes a hidden
sidecar (`.<clip>.qc.json`) beside every clip saying where it came from. Point a Jellyfin
library at that folder and, without this plugin, each clip shows up as its file name
(`00-00-46_No_it_doesn't_really_matter_Crank_Yankers`) with a screen grab. With it:

| | Without | With the plugin |
|---|---|---|
| **Title** | the file name | the line: *No it doesn't really matter* |
| **Image** | a screen grab | the source episode's still, or the movie's poster |
| **Overview** | none | *Crank Yankers · S01E01 · Tenacious D & Wanda Sykes · at 0:46 · cut 2026-06-21* |
| **Tags** | none | the show, so "every clip from Crank Yankers" is a tag filter |
| **Year, date** | none | the source's year; the day it was cut as the release date (sort newest first) |
| **Folders** | the release's folder name | the source they share (*Bambi (1942)*), or the show, with its poster |

It reads the sidecars off disk: no network, no account, nothing to configure. A clip with
no sidecar, or one it can't read, keeps Jellyfin's defaults; nothing the plugin does can
fail a library scan.

**Subtitles Jellyfin brings in become searchable in quipclipper within a minute.** quipclipper
searches only indexed files, and a file whose subtitles change drops out of search until it is
re-indexed. Once the plugin is connected to quipclipper, a subtitle Jellyfin downloads (its
*Download missing subtitles* task, a search in an item's subtitle dialog) or notices dropped
into a library makes the plugin queue that file for re-indexing in quipclipper, with no button
pressed.

## Install

1. In Jellyfin: **Dashboard › Plugins › Repositories › +**, name it `quipclipper`, URL
   `https://weckere.github.io/jellyfin-plugin-quipclipper/manifest.json`.
2. **Catalog › quipclipper › Install**, then restart Jellyfin.
3. Have a library whose folder is quipclipper's clips folder (type **Home Videos**; it may be
   mounted read-only). Run **Scan library › Replace all metadata** on it once, with
   **Replace existing images** ticked, so the clips Jellyfin already knows pick up their
   lines and artwork. New clips get theirs when Jellyfin scans them (quipclipper asks for a
   scan of the Clips library after each cut).

Later versions arrive through Jellyfin's **Update Plugins** task.

### Connect to quipclipper (subtitle re-index)

1. **Dashboard › Plugins › quipclipper**: enter quipclipper's URL as *this server* reaches it
   (from a container, its address on Jellyfin's network), and press **Connect**.
2. quipclipper's pages show a banner, *Jellyfin on (server) wants to queue subtitle
   re-indexes and read transcripts, code 7F3K*. Press **Allow** if the code matches the one
   on the plugin page.
3. Optionally tick the libraries to watch (none ticked: all) and **Save**.

The plugin pairs with quipclipper's scoped client tokens (quipclipper with scopes; see its
docs/API.md, Pairing): its key can queue re-indexes and read transcripts and nothing else, and
it is kept in the plugin's configuration (readable by Jellyfin and its admins). **Disconnect**
revokes it, as does Revoke under quipclipper's Settings › Connected apps. quipclipper maps
Jellyfin's paths with its Jellyfin path map, which it derives itself when none is set.

If you change the library's settings, keep **quipclipper** ticked under *Image fetchers*
for Video and Folder (and above *Screen Grabber*).

## How it works

- `ClipMetadataProvider` and `ClipFolderMetadataProvider` are local metadata providers for
  plain `Video` and `Folder` items (what a Home Videos library holds; movies, episodes and
  series are never touched). They read the sidecar, and report a change when it is newer
  than Jellyfin's copy, so an edited sidecar refreshes its clip on the next scan.
- `ClipImageProvider` looks the source up by the Jellyfin id in the sidecar and hands
  Jellyfin its artwork **as a stream**, so Jellyfin keeps its own copy in the clip's
  metadata folder. Never a path: Jellyfin deletes the file behind a provider's image path
  after copying it, and deleting or replacing an image that points at another item's file
  deletes that file.
- The sidecar schema is quipclipper's `quipclipper.clip-meta` v1 (quipclipper's
  docs/API.md, "The metadata sidecar"). Every field but a few is optional and may be `null`.

### How the subtitle watcher works

- `SubtitleWatcher` (a hosted service) notes every video and audio file Jellyfin adds or
  updates; every way a subtitle arrives ends in an item update. It does nothing else on
  Jellyfin's thread.
- `SubtitleWatch`, on a loop of its own, waits until a burst of updates is over (10 s after
  the last, at most 30 s after the first), lists each noted file's folder, and compares its
  subtitle files (the ones named like it, as quipclipper finds them, with their write times)
  with what it saw before. Only a change is sent: `POST /api/index/file` with
  `{"paths": [...], "source": "jellyfin"}`, at most 1,000 paths a request.
- The first time it watches a library folder it walks it once to note what is there, and
  sends nothing for it. Its state (that baseline, and what waits) is
  `plugins/configurations/quipclipper-subtitles.json` in Jellyfin's data folder.
- quipclipper down or failing: what waits is kept (up to 10,000 files) and retried after 30 s,
  doubling up to 10 minutes. A 401 or 403 (revoked key) stops it until the server connects
  again; the plugin page says so.

## Build and test

```bash
dotnet test
```

The plugin builds against the `Jellyfin.Controller`/`Jellyfin.Model` **12.0.0** packages
(the floor: Jellyfin loads a plugin on any server at least its `targetAbi`, 12.0.0.0). CI
also runs the tests against the newest 12.x packages: `dotnet test -p:JellyfinVersion=12.*`.

## Releasing

Work lands on `dev`; `main` only moves at a release. Every change that does something adds
a line under `## [Unreleased]` in [CHANGELOG.md](CHANGELOG.md).
`scripts/release.sh X.Y.Z.W` (on a clean, pushed `dev`; `--dry-run` to rehearse) sets the
version, dates the changelog, tags, and pushes `dev`, `main` and the tag. The tag's
workflow builds the zip (the DLL with `meta.json`), attaches it to a GitHub Release with
`version.json` (its manifest entry and MD5), and republishes `manifest.json` on GitHub
Pages from every release.

## Licence

MIT. See [LICENSE](LICENSE).
