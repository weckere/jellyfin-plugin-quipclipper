# Changelog

All notable changes to jellyfin-plugin-quipclipper are written down here. The format
follows [Keep a Changelog](https://keepachangelog.com/en/1.1.0/). Versions are Jellyfin's
four parts, `X.Y.Z.0` (still 0.x: a minor version for features, a patch for fixes).

Every commit on `dev` that changes behaviour adds a line under **Unreleased**;
`scripts/release.sh X.Y.Z.W` turns that section into the release's, dated.

## [Unreleased]

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
