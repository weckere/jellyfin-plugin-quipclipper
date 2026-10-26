# jellyfin-plugin-quipclipper

A [Jellyfin](https://jellyfin.org) 12.x plugin for [quipclipper](https://github.com/weckere/quipclipper),
the dialogue-search clip cutter. It does three things, each on its own:

1. **Clips by their line.** quipclipper's clips show in Jellyfin named by the line they hold,
   with the source's artwork. Needs no connection to quipclipper.
2. **Subtitles searchable at once.** When Jellyfin brings in a subtitle, quipclipper re-indexes
   that file within a minute. Needs the plugin [connected](#connect-to-quipclipper).
3. **Machine transcripts as a subtitle source.** Jellyfin's subtitle search offers
   quipclipper's machine transcript of a movie or episode. Needs the connection too.

## Contents

- [What each feature does](#what-each-feature-does)
- [Requirements](#requirements)
- [Install](#install)
- [Clip metadata: set up the clips library](#clip-metadata-set-up-the-clips-library)
- [Connect to quipclipper](#connect-to-quipclipper)
- [When Jellyfin runs in a container](#when-jellyfin-runs-in-a-container)
- [Paths: Jellyfin's and quipclipper's](#paths-jellyfins-and-quipclippers)
- [What it writes, and where](#what-it-writes-and-where)
- [Troubleshooting](#troubleshooting)
- [Jellyfin versions](#jellyfin-versions)
- [How it works](#how-it-works)
- [Build and test](#build-and-test) · [Releasing](#releasing) · [Licence](#licence)

## What each feature does

### Clips by their line

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

It reads the sidecars off disk: no network, no account. A clip with no sidecar, or one it
can't read, keeps Jellyfin's defaults.

### Subtitles searchable at once

quipclipper searches only indexed files, and a file whose subtitles change drops out of
search until it is re-indexed. Once connected, the plugin notices every subtitle Jellyfin
brings in (its *Download missing subtitles* task, a search in an item's subtitle dialog, a
`.srt` dropped into a library folder) and asks quipclipper to re-index that file. No button,
no script.

### Machine transcripts as a subtitle source

For a movie or episode quipclipper has transcribed (whisper), an item's **Edit subtitles ›
Search** lists results from *quipclipper (machine transcript)*: **Machine lines only** (the
lines no subtitle has) and **Full machine transcript**, named with the model and how much was
heard. Picking one saves it as `<name>.en.whisper.srt`, a separate "whisper" track that
quipclipper reads as machine lines, never as the file's subtitles. Only a search someone runs
gets results: Jellyfin's automatic downloads never do, and nothing starts a transcription.

## Requirements

- **Jellyfin 12.0 or later** (tested on 12.0 through 12.3; see [Jellyfin versions](#jellyfin-versions)).
- **Clip metadata:** any quipclipper that writes clip sidecars (0.2 or later), and a Jellyfin
  library on its clips folder.
- **Re-index and transcripts:** a quipclipper with scoped client tokens and Jellyfin paths
  (`"source": "jellyfin"`): the release after 0.4.1, or its `dev` branch until then. An older
  quipclipper refuses to pair ("has no scoped tokens"). Jellyfin must be able to reach
  quipclipper over HTTP.

## Install

1. In Jellyfin: **Dashboard › Plugins › Repositories › +**, name it `quipclipper`, URL
   `https://weckere.github.io/jellyfin-plugin-quipclipper/manifest.json`.
2. **Catalog › quipclipper › Install**, then restart Jellyfin.
3. **Dashboard › Plugins › quipclipper** is its settings page.

Later versions arrive through Jellyfin's **Update Plugins** task (restart Jellyfin to load
them). To remove it: **Dashboard › Plugins › quipclipper › Uninstall**, and press
**Disconnect** on its page first so quipclipper forgets its key.

## Clip metadata: set up the clips library

1. Add a library of type **Home Videos** whose folder is quipclipper's clips folder (as
   Jellyfin sees it; it may be mounted read-only).
2. Run **Scan library › Replace all metadata** on it once, with **Replace existing images**
   ticked, so clips Jellyfin already knows pick up their lines and artwork. New clips get
   theirs when Jellyfin scans them (quipclipper asks Jellyfin to scan the clips library after
   each cut when it has Jellyfin's URL and an API key).

If you change the library's settings, keep **quipclipper** ticked under *Image fetchers* for
Video and Folder, above *Screen Grabber*. Movies, episodes and series are never touched.

## Connect to quipclipper

Re-index and transcripts need the plugin paired with quipclipper. Pairing hands it a key of
its own; there is no token to copy.

1. On the plugin's page, enter quipclipper's URL **as this Jellyfin server reaches it**
   (Jellyfin calls quipclipper, not your browser), for example `http://quipclipper:8080`.
   Or press **Find on this network**: it lists quipclipper servers that advertise themselves
   on the LAN (quipclipper's `QC_ADVERTISE=1`) and fills in the one you pick.
2. Press **Connect**. The page shows a four-letter code.
3. In quipclipper, a banner says *Jellyfin on (server) wants to queue subtitle re-indexes and
   read transcripts, code 7F3K*. Press **Allow** if the code matches. The page says
   *Connected* within a few seconds. (Closed the page meanwhile? Open it again: it picks up
   the request.)
4. Optionally tick the libraries to watch (none ticked: all) and press **Save**.

**What the key may do.** It is scoped to `index.file` and `transcripts.read`: queue
re-indexes and read transcripts, nothing else; quipclipper answers 403 to everything else.
Jellyfin keeps it in the plugin's configuration file, which only Jellyfin and its
administrators can read. **Disconnect** on the plugin's page revokes it, as does Revoke under
quipclipper's **Settings › Connected apps**.

To leave the transcripts out of a library's subtitle search, untick *quipclipper (machine
transcript)* under that library's *Subtitle downloaders*.

### Finding quipclipper on the network

**Find on this network** sends a one-shot mDNS query for `_quipclipper._tcp` from Jellyfin
and lists what answers, the advertised address first. It is opt-in on both sides: quipclipper
advertises only with `QC_ADVERTISE=1`, and the plugin asks only when you press the button; it
announces nothing and listens on no fixed port. Multicast doesn't cross a container's bridge
network, so a Jellyfin in Docker finds nothing unless it uses the host's network. Typing the
URL always works.

## When Jellyfin runs in a container

Jellyfin must reach quipclipper over HTTP from wherever it runs:

- **Same Docker network:** use quipclipper's service name, e.g. `http://quipclipper:8080`.
- **Different networks:** use an address Jellyfin's container can route to, such as the
  host's address on the Docker bridge, and check from inside the container:
  `docker exec jellyfin curl -s http://<address>:<port>/api/health`.
- **quipclipper behind a password gate:** that works as it is; pairing and client keys pass
  quipclipper's gate.
- **quipclipper deliberately out of reach** (a tailnet, a network Jellyfin isn't on): open a
  listener for Jellyfin's address alone that passes only the routes the plugin uses, and
  refuse everything else:

  | Route | Used for |
  |---|---|
  | `POST /api/pair`, `GET /api/pair/<id>` | pairing |
  | `POST /api/index/file` | re-index requests |
  | `GET /api/items/transcript` | the transcript provider |
  | `DELETE /api/clients/self` | Disconnect |

  For example, with nginx in front of quipclipper:

  ```nginx
  server {
      listen 172.18.0.1:8090;          # an address Jellyfin's container reaches
      allow 172.18.0.5; deny all;      # Jellyfin's container only
      location = /api/pair                { proxy_pass http://127.0.0.1:8000; }
      location ~ ^/api/pair/[A-Za-z0-9_-]+$ { proxy_pass http://127.0.0.1:8000; }
      location = /api/index/file          { limit_except POST { deny all; } proxy_pass http://127.0.0.1:8000; }
      location = /api/items/transcript    { limit_except GET  { deny all; } proxy_pass http://127.0.0.1:8000; }
      location = /api/clients/self        { limit_except DELETE { deny all; } proxy_pass http://127.0.0.1:8000; }
      location / { return 404; }
  }
  ```

  and give the plugin `http://172.18.0.1:8090` as quipclipper's URL. Keep `X-API-Key` passed
  through (nginx does by default).

## Paths: Jellyfin's and quipclipper's

The plugin sends Jellyfin's own paths (`/media/tv/Show/S01E01.mkv`) and says so
(`"source": "jellyfin"`); quipclipper turns them into its own with its Jellyfin path map.
Set `QC_JELLYFIN_PATH_MAP` in quipclipper (`/media/tv=/srv/tv:/media/movies=/srv/movies`),
or leave it unset and give quipclipper Jellyfin's URL and an API key (`QC_JELLYFIN_URL`,
`QC_JELLYFIN_API_KEY`): it then derives the map by finding a few files of each Jellyfin
library folder under its own media roots. quipclipper's `GET /api/jellyfin/lookup` shows the
map in use and any folder it couldn't place (`unmatched`); a file in such a folder is refused
until the map names it.

## What it writes, and where

| Feature | Writes | Where |
|---|---|---|
| Clip metadata | each clip's title, overview, tags, dates; the source's artwork, copied | Jellyfin's database and the clip's own metadata folder. Nothing beside the clips; the sidecars are only read. |
| Settings, pairing | quipclipper's URL, the key, its scopes, the libraries to watch | `plugins/configurations/Jellyfin.Plugin.Quipclipper.xml` in Jellyfin's data folder |
| Subtitle re-index | what subtitles each media file has (names and write times, no contents), and what waits to be sent | `plugins/configurations/quipclipper-subtitles.json` in Jellyfin's data folder. quipclipper receives media paths only. |
| Machine transcripts | the transcript picked, as `<name>.en.whisper.srt` (Jellyfin writes it) | beside the media when the library's *Save subtitles with media* is on (the default), else in Jellyfin's metadata folder for the item |

The plugin never deletes a file, and never writes to the media or clips folders itself.

## Troubleshooting

| What you see | Why, and what to do |
|---|---|
| Connect: *quipclipper is unreachable* | Jellyfin can't reach that URL. Test it from where Jellyfin runs ([above](#when-jellyfin-runs-in-a-container)). |
| Connect: *has no scoped tokens* (`too_old`) | quipclipper is older than scoped client tokens: update it. If it says it still lists Jellyfin, revoke that under Connected apps. |
| *The request expired* | Nobody pressed Allow within 10 minutes, or quipclipper restarted meanwhile (it forgets waiting requests). Connect again. |
| *Stopped: quipclipper refused the token* | The key was revoked in quipclipper. Connect again. |
| Subtitles still not searchable | quipclipper's **Re-index changed subtitles** job, or `quipclipper index.file-status`, shows each file's result. `outside_roots`: the path map doesn't cover that library folder ([Paths](#paths-jellyfins-and-quipclippers)). The plugin's page shows what waits and the last send. |
| Nothing sent for subtitles that were already there | By design: the first time it watches a library it notes what is there and sends nothing. Index those in quipclipper. |
| **Find on this network** finds nothing | quipclipper doesn't advertise (`QC_ADVERTISE=1`), or Jellyfin is in a container without the host's network, or they are on different LANs. Type the URL. |
| No *quipclipper* results in subtitle search | Only manual searches get them; the item must be a movie or episode quipclipper has transcribed in the language searched, with no `.whisper.srt` yet, and the library's *Subtitle downloaders* must include it. |
| Clips still show file names | The library must be **Home Videos** (plain videos), and needs a **Replace all metadata** scan once. A clip whose sidecar is missing keeps its file name. |
| *Off until Jellyfin restarts: …* on the plugin's page | A Jellyfin update removed something that feature uses; it logged the details once and turned itself off. The other features carry on. Update the plugin. |

Jellyfin's log has a line for each of these, starting with `quipclipper:`.

## Jellyfin versions

The plugin is built against Jellyfin 12.0.0 and tested in CI against both 12.0.0 and the
newest 12.x packages. Jellyfin loads it on any server at least its `targetAbi` (12.0.0.0),
including a newer major, so it guards itself: every place Jellyfin calls into it (the
metadata and image providers, the item events, the subtitle provider, its service
registration) runs behind a per-feature guard, and a missing method or changed type turns
just that feature off, logged once, instead of failing a library scan. There is no Jellyfin
13 yet; what its development branch changes for this plugin is in
[docs/jellyfin-13.md](docs/jellyfin-13.md).

## How it works

### Clip metadata

- `ClipMetadataProvider` and `ClipFolderMetadataProvider` are local metadata providers for
  plain `Video` and `Folder` items (what a Home Videos library holds). They read the
  sidecar, and report a change when it is newer than Jellyfin's copy, so an edited sidecar
  refreshes its clip on the next scan.
- `ClipImageProvider` looks the source up by the Jellyfin id in the sidecar and hands
  Jellyfin its artwork **as a stream**, so Jellyfin keeps its own copy in the clip's metadata
  folder. Never a path: Jellyfin deletes the file behind a provider's image path after
  copying it, and deleting or replacing an image that points at another item's file deletes
  that file.
- The sidecar schema is quipclipper's `quipclipper.clip-meta` v1 (quipclipper's
  docs/API.md, "The metadata sidecar"). Every field but a few is optional and may be `null`.

### The subtitle watcher

- `SubtitleWatcher` (a hosted service) notes every video and audio file Jellyfin adds or
  updates; every way a subtitle arrives ends in an item update. It does nothing else on
  Jellyfin's thread.
- `SubtitleWatch`, on a loop of its own, waits until a burst of updates is over (10 s after
  the last, at most 30 s after the first), lists each noted file's folder, and compares its
  subtitle files (the ones named like it, as quipclipper finds them, with their write times)
  with what it saw before. Only a change is sent: `POST /api/index/file` with
  `{"paths": [...], "source": "jellyfin"}`, at most 1,000 paths a request.
- The first time it watches a library folder it walks it once to note what is there, and
  sends nothing for it.
- quipclipper down or failing: what waits is kept (up to 10,000 files) and retried after
  30 s, doubling up to 10 minutes. A 401 or 403 (revoked key) stops it until the server
  connects again; the plugin page says so.

### The transcript provider

- `TranscriptSubtitleProvider` is an `ISubtitleProvider` for movies and episodes, registered
  through DI. A search with `IsAutomated` (the *Download missing subtitles* task and
  scan-time downloads, which take the first result they get) answers nothing at once, as
  does an item with a machine sidecar already, beside it (`<name>[.lang].whisper.srt`/`.vtt`)
  or in Jellyfin's metadata folder for it.
- Otherwise it asks quipclipper `GET /api/items/transcript?source=jellyfin&lines=machine`
  (the `transcripts.read` scope) for what it has stored. No transcript, a copy already in
  the library, another language, or quipclipper unreachable: nothing, logged, never thrown.
- A pick downloads `fmt=srt` (`lines=machine` or `all`) and answers with language
  `en.whisper`. Jellyfin 12.x names the saved file `<stem>.<language>.srt` as given, so it
  lands as `<stem>.en.whisper.srt`. A pick that can't be served fails in the dialog rather
  than saving something wrong. The ids it hands Jellyfin are signed with its key, so a
  download can only fetch a file a search offered.
- A pick saved in Jellyfin's metadata folder (*Save subtitles with media* off) isn't seen by
  quipclipper, which only reads beside the media; that costs nothing, since the lines are
  quipclipper's own transcript.

### Pairing and discovery

- `PairingService` speaks quipclipper's `quipclipper.pair/1` from Jellyfin's side, so the
  browser never talks to quipclipper (no CORS). Polls run one at a time, so two open
  settings pages get the same answer.
- `Discovery` sends one-shot mDNS queries (RFC 6762 §5.1) on each interface that can
  multicast and reads the unicast answers: PTR, then SRV, TXT and A, asking for any an
  answer left out.

### The guard

`Feature` wraps each entry point. An exception that says this Jellyfin doesn't match what
the plugin was built against (`MissingMethodException`, `MissingFieldException`,
`TypeLoadException`, an assembly that won't load) turns that feature off until Jellyfin
restarts and is logged once; any other error is handled where it happens (one item's
unreadable sidecar, quipclipper unreachable). `GET /Quipclipper/Status` lists the features
and why any is off.

## Build and test

```bash
dotnet test
```

The plugin builds against the `Jellyfin.Controller`/`Jellyfin.Model` **12.0.0** packages
(the floor: Jellyfin loads a plugin on any server at least its `targetAbi`, 12.0.0.0). CI
also runs the tests against the newest 12.x packages: `dotnet test -p:JellyfinVersion=12.*`.
The tests are deterministic: the watcher's loop runs on a fake clock, discovery against a
loopback advertiser and python-zeroconf's own answer bytes.

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
