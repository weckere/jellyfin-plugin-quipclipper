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
