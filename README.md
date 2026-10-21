# DLSS5 Updater catalog

The data side of [DLSS5 Updater](https://github.com/ApolloF/DLSS5-Updater): one `catalog.json` that says which community
mod files exist, which game each belongs to, and the exact SHA-256 of every build. A GitHub Action rebuilds it every six
hours and commits it only when something changed. The app reads it through GitHub's ETag cache, keeps the last good copy,
and refuses any download whose hash or size doesn't match.

Upstream "snapshot" builds have no version numbers. The catalog gives each build one (the date it was first seen plus
the first seven characters of its hash) and keeps the hashes of earlier builds, so the app can show "updated" and "what
changed when".

## What is in it

`catalog.json` has a schema version and independent sections. `renodx` and `renodxDlss` exist so far; later areas (Luma, add-ons,
shader packs, the DLSS5 feeder, OptiScaler compatibility) are added as new sections without touching this one. A reader
uses the sections it knows and ignores the rest. The machine-readable description is
[`schema/catalog.schema.json`](schema/catalog.schema.json) (JSON Schema 2020-12); the tests validate the generator's
output against it.

```
{
  "schema": 1,                       readers refuse a higher number
  "repository": "ApolloF/DLSS5-Updater-catalog",
  "generated": "2026-10-21T12:00:00Z",
  "sections": {
    "renodx": {
      "updated": "...",              when this section last changed
      "sources": [ { "url", "count" } ],
      "generic": [ ... ],            Unreal, Unity (64/32-bit) and Unreal Extended add-ons
      "mods":    [ ... ],            one entry per game
      "engineGames": [ ... ],        games the wiki lists as working with a generic add-on
      "overrides": { ... }           copy of overrides.json, for the app's matcher
    },
    "renodxDlss": {
      "updated": "...",
      "source": "...",               the releases API url the builds were read from
      "builds": [ ... ]              ShortFuse's renodx-dlss add-on, newest first (at most 10)
    }
  }
}
```

### `renodx`

Sources, all read directly from their authors:

| What | Where |
|---|---|
| Game list with Steam AppIDs and file names | `https://github.com/clshortfuse/renodx/releases/download/snapshot/games-index.json` |
| Maintainers, status marks, links, notes, mods the index lacks, per-engine game tables | `https://raw.githubusercontent.com/wiki/clshortfuse/renodx/Mods.md` |
| Generic Unreal | `https://github.com/clshortfuse/renodx/releases/download/snapshot/renodx-unrealengine.addon64` |
| Generic Unity (64-bit, 32-bit) | `https://github.com/NotVoosh/renodx-unity/releases/download/snapshot/renodx-unityengine.addon64` / `.addon32` |
| Unreal Extended | `https://marat569.github.io/renodx/renodx-ue-extended.addon64` |

The wiki table is parsed as Markdown (name, maintainer, links, status, notes), not scraped as HTML. A wiki row is joined
to the index entry with the same file name, else the same normalised name (lowercase letters and digits only). Rows the
index doesn't have become entries of their own; their files come from the snapshot links in the row.

A **mod** entry:

| Field | Meaning |
|---|---|
| `id`, `name`, `aliases` | Stable id (index game id, or a slug of the wiki name), display name, other titles that match |
| `steamAppIds` | Steam AppIDs from the index (empty for wiki-only entries) |
| `authors`, `status`, `compat`, `notes`, `links` | Maintainers; upstream's rating (`stable`/`beta`/`experimental`); `working` or `in-progress`; plain-text notes; Nexus/Discord/discussion/GameBanana links |
| `listing` | `main`, `related`, `engine` or `deprecated`. Only `main` and `engine` entries are meant to be installed |
| `generic` | `unreal` or `unity`: the game has no file of its own and upstream points it at the generic add-on |
| `blocked`, `blockReason` | Set by `overrides.json`; a blocked entry has no files |
| `files` | The downloadable binaries (below) |

An **engineGames** entry is one row of a wiki per-engine table: `engine` (`ue-extended`, `unreal`, `unity`, `source`),
`name`, `compat` and the wiki's `notes` as written. For `ue-extended` the notes are also read into optional fields
(the note stays the source of truth; a field is absent when the note doesn't say):

| Field | Meaning |
|---|---|
| `method` | `upgrade` (note says `Upgrade Path: On`), else `native` (`Upgrade Path: Off` or `Native HDR`), else `ini` (mentions `Engine.ini`) |
| `upgrades` | `[ { "format": "B8G8R8A8_TYPELESS", "mode": "output-size" } ]`. `mode` is `output-size`, `output-ratio` or `any-size`, the words right after the format. Only the 12 formats the add-on knows are listed; anything else (a typo such as `R8G8R8A8_TYPELESS`) is dropped and reported as a warning in the run log |
| `copyDestinations` | `false` for `Upgrade Copy Destinations Off`, `true` for `On` |
| `engineIni` | `[ { "section": "SystemSettings", "key": "r.HDR.EnableHDROutput", "value": "1" } ]` for every `r.Key=Value` in the note |

A **file** entry:

| Field | Meaning |
|---|---|
| `name`, `arch` (`x64`/`x86`), `url` | File name, bitness, and the author's own download address |
| `sha256`, `size` | Hash and byte size of the current build. A reader installs only entries that have them and refuses a mismatch |
| `version`, `firstSeen` | `2026-10-21-ab12cd3`: UTC date this build was first seen plus short hash; the exact time |
| `history` | Earlier builds, newest first: `version`, `sha256`, `size`, `firstSeen`, `mirrorUrl` (at most 20) |
| `license` | SPDX id of the source repository's licence, if GitHub reports one |
| `mirrorUrl` | Copy in this repository's release assets. **Absent = pinned**: only the upstream URL and the hash are known |
| `verified`, `etag`, `lastModified` | When the hash was last confirmed by a full download, and the HTTP validators used to skip re-downloads |
| `mod` | The index mod the file belongs to |

Every binary is downloaded by the generator, hashed, size-checked and must start with `MZ` (an HTML error page is never
recorded as a build). The one exception is a zip (`PK`), accepted only when its central directory lists an entry
named `renodx-dlss.addon64`. A file that fails keeps its last good entry, or is left out if it never had one.

### Mirror or pinned

If the source repository's licence is MIT, BSD-2-Clause, BSD-3-Clause, Apache-2.0, ISC or the Unlicense (GitHub API
`license.spdx_id`; `https://<owner>.github.io/<repo>/...` is looked up as `<owner>/<repo>`), the file is uploaded as an
immutable release asset of this repository and `mirrorUrl` points at it. Assets live in one release per month
(`mirror-2026-10`, by the month the build was first seen), are named `<first 16 hash characters>-<file name>`, and are
never overwritten or replaced. Any other licence, no licence, or a source we can't attribute to a repository: no copy is
made, the entry keeps the upstream `url` plus the hash, and the app refuses the file if upstream changes it before the
next run.

The mirrored files belong to their authors and stay under their licences. They are copies for reliability and hash
pinning, not a new distribution: see each file's `license` and the source repositories (for example
[clshortfuse/renodx](https://github.com/clshortfuse/renodx), [NotVoosh/renodx-unity](https://github.com/NotVoosh/renodx-unity),
[marat569/renodx](https://github.com/marat569/renodx)) for the licence texts and credits. This repository never hosts or lists
NVIDIA's `nvngx_dlssnr.dll` or any other NVIDIA DLL.

### `renodxDlss`

ShortFuse's `renodx-dlss.addon64` has no official download channel. The builds are published as GitHub releases of
`RankFTW/rhi-repo`, tagged `renodx-dlss-SF-<version>` (for example `26.1003.2350`, older ones `0.55`). Each release has one
asset, a **zip** named `renodx-dlss_SF_<version>.zip` whose only entry is `renodx-dlss.addon64` at the root (about 1 MB).
The generator reads `https://api.github.com/repos/RankFTW/rhi-repo/releases?per_page=100`, keeps the releases with that tag
that are neither drafts nor prereleases and have exactly one `.zip` asset, and lists the newest 10 by publish date.

```json
{
  "updated": "2026-10-21T12:00:00Z",
  "source": "https://api.github.com/repos/RankFTW/rhi-repo/releases?per_page=100",
  "builds": [
    {
      "tag": "renodx-dlss-SF-26.1003.2350",
      "version": "26.1003.2350",
      "published": "2026-10-03T23:55:00Z",
      "file": { "name": "renodx-dlss_SF_26.1003.2350.zip", "arch": "x64", "url": "https://github.com/RankFTW/rhi-repo/releases/download/...", "sha256": "...", "size": 1000000, "version": "2026-10-21-ab12cd3", "firstSeen": "...", "mod": "renodx-dlss", "history": [] }
    }
  ]
}
```

`file` is a normal file entry (above), so it has the hash, size, first-seen version, history and ETag like the others, but the
repository has no licence: the build is **pinned, never mirrored**, so there is never a `mirrorUrl`. A reader downloads the
zip from `url`, checks the hash, and takes `renodx-dlss.addon64` out of it. A zip without that entry is refused like a bad
download. If the release list is unreachable, isn't a list or has no matching release, the run fails and the previous
section stays; a single build whose download fails is a warning and keeps its last good entry.

### Being polite upstream

- Binaries are fetched with `If-None-Match` / `If-Modified-Since`; a 304, or a 200 with the same ETag and size, is not read.
- A file is downloaded in full again only when its size or ETag changed, when a mirror copy is missing, or once a week
  (`verified`) to catch changes behind an unchanged ETag.
- At most four downloads run at once. The two list files (about 750 KB together) are read every run.
- API calls use the Action's `GITHUB_TOKEN`; licence lookups happen once per repository per run.
- If a list is unreachable, isn't valid, or looks truncated (fewer than 50 games, fewer than 100 wiki rows, or more than 20%
  of the mods gone), the run **fails** and the previous section stays in `catalog.json`. Single dead file links are
  warnings, not failures. A failed mirror upload fails the run and is retried on the next one.

## `overrides.json`

Hand-maintained. Merged into the output on every run, and copied to `renodx.overrides` so the app can apply it when it
matches games. Start empty; every list is optional.

```json
{
  "schema": 1,
  "renodx": {
    "nameFixes":        [ { "id": "alien-isolation", "name": "Alien: Isolation", "aliases": ["Alien Isolation GOTY"] } ],
    "appIdFixes":       [ { "id": "some-game", "steamAppIds": [12345] } ],
    "blocklist":        [ { "id": "some-game", "reason": "crashes on start" }, { "name": "Other Game" } ],
    "ueExtendedOptOut": [ { "steamAppId": 12345, "reason": "no HDR with Unreal Extended" }, { "name": "Some Game" } ],
    "engineIniPaths":   [ { "steamAppId": 12345, "path": "Game\\Saved\\Config\\Windows\\Engine.ini" } ]
  }
}
```

| List | Effect |
|---|---|
| `nameFixes` | Entry `id` gets the new `name` (the old one becomes an alias) and extra `aliases` (store titles that should match it) |
| `appIdFixes` | Replaces the entry's `steamAppIds` |
| `blocklist` | The entry (by `id`, or by normalised `name`) is flagged `blocked` and keeps no files; the app never offers it |
| `ueExtendedOptOut` | Games (by AppID or name) that must not get Unreal Extended by default; passed to the app as is |
| `engineIniPaths` | Where a game's `Engine.ini` lives; relative paths are under `%LOCALAPPDATA%`, passed to the app as is |

An `id` that matches no entry is reported as a warning in the run log.

## Running it

The `catalog` workflow runs on a six-hour schedule and by hand (Actions → catalog → Run workflow). It tests, runs the
generator, and commits `catalog.json` if it changed. Pull requests and pushes run the tests only.

Locally (.NET 8 SDK):

```
dotnet test
dotnet run --project src/CatalogGen -- --no-mirror          # hashes and pins, uploads nothing
```

`--catalog`, `--overrides`, `--temp` and `--repo` change the paths; `GITHUB_TOKEN` raises the API rate limit and, with
`--repo`, enables mirror uploads. `CATALOG_ACCEPT_SHRINK=1` accepts a section that lost more than 20% of its mods.

## Layout

```
catalog.json              generated, committed by the Action
overrides.json            hand-maintained
schema/catalog.schema.json
src/CatalogGen            the generator (.NET 8 console app)
tests/CatalogGen.Tests    xUnit: wiki parsing, merge, licence decision, history, mirror, schema
.github/workflows/catalog.yml
```

The generator code is MIT (see [LICENSE](LICENSE)); the mirrored files are not part of it.
