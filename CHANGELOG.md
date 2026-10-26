# Changelog

All notable changes to jellyfin-plugin-quipclipper are written down here. The format
follows [Keep a Changelog](https://keepachangelog.com/en/1.1.0/). Versions are Jellyfin's
four parts, `X.Y.Z.0` (still 0.x: a minor version for features, a patch for fixes).

Every commit on `dev` that changes behaviour adds a line under **Unreleased**;
`scripts/release.sh X.Y.Z.W` turns that section into the release's, dated.

## [Unreleased]

### Machine transcripts as a subtitle source (quipclipper M22, Phase 3)

- **Jellyfin's subtitle search offers quipclipper's machine transcripts.** For a movie or
  episode quipclipper has transcribed, an item's subtitle search lists *quipclipper (machine
  transcript)* results, "Machine lines only" (the lines no subtitle has) and "Full machine
  transcript", with the model, how much was heard and the line count. The one picked is saved
  beside the media as `<name>.en.whisper.srt`, which quipclipper reads as machine lines and
  Jellyfin shows as its own track.
- **Manual searches only:** Jellyfin's automatic subtitle downloads get nothing, an item with
  no stored transcript or with a `.whisper.srt` already gets nothing, and it never starts a
  transcription. Uses the existing connection (scope `transcripts.read`); needs quipclipper
  with `lines=` and `source=jellyfin` on `GET /api/items/transcript`.

## [0.2.0.0] - 2026-10-25

### Re-index changed subtitles (quipclipper M22, Phase 2)

- **Subtitles Jellyfin brings in become searchable in quipclipper within a minute.** A
  subtitle Jellyfin downloads (its "Download missing subtitles" task, a search in an item's
  subtitle dialog) or notices dropped into a library makes the plugin send that file to
  quipclipper's re-index queue, with no button pressed. Only real changes are sent (a
  subtitle file added, removed or rewritten): the first run notes what every library holds
  and sends nothing; a burst of item updates (a library scan) is sent ~10 s after it ends, at
  most 30 s after it starts, 1,000 files a request. While quipclipper is down, up to 10,000
  files wait (kept across restarts) and are retried after 30 s, doubling up to 10 minutes.
- **Connect on the plugin's settings page.** Enter quipclipper's URL and press Connect; allow
  the code in quipclipper's banner. The plugin pairs with scopes `index.file` and
  `transcripts.read`, so its key can only queue re-indexes and read transcripts (needs
  quipclipper with scoped client tokens; an older quipclipper's full-access token is handed
  back, not kept). Disconnect revokes it. Pick the libraries to watch
  (none ticked: all), or switch sending off.
- Everything runs on a background loop of its own with short timeouts; nothing can fail or
  slow a library scan. A revoked key stops sending until the server connects again.

## [0.1.0.0] - 2026-10-25

### Clip metadata (quipclipper M22, Phase 1)

- **Clips show their line.** In a library pointed at quipclipper's clips folder, each clip
  is titled by its line, with an overview of where and when it's from (show, episode,
  timestamp, the day it was cut), the show as a tag, the source's year, and the cut date as
  its release date. All read from the hidden sidecar quipclipper writes beside each clip;
  no network.
- **Clips show their source's artwork:** the episode's still or the movie's poster, copied
  into the clip's own metadata (the source's image files are never touched).
- **Per-source folders are named after their source** ("Bambi (1942)",
  "Crank Yankers · S01E01 · …") or the show they share, with its poster.
- A missing, unreadable or foreign sidecar leaves Jellyfin's defaults; no failure reaches a
  library scan.
