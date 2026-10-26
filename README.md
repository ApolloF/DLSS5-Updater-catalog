# DLSS5 Updater catalog

The data side of [DLSS5 Updater](https://github.com/ApolloF/DLSS5-Updater): one `catalog.json` that says which community
mod files exist, which game each belongs to, and the exact SHA-256 of every build. A GitHub Action rebuilds it every six
hours and commits it only when something changed. The app reads it through GitHub's ETag cache, keeps the last good copy,
and refuses any download whose hash or size doesn't match.

Upstream "snapshot" builds have no version numbers. The catalog gives each build one (the date it was first seen plus
the first seven characters of its hash) and keeps the hashes of earlier builds, so the app can show "updated" and "what
changed when".

## What is in it

`catalog.json` has a schema version and independent sections. `renodx`, `renodxDlss`, `neural`, `optiscalerCompat`, `addons`, `frameLimiters`, `luma` and `shaderPacks` exist so far; later areas
(the DLSS5 feeder) are added as new sections without touching these. A reader
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
      "builds": [ ... ]              ShortFuse's renodx-dlss add-on, newest first (at most 20)
    },
    "neural": {
      "updated": "...", "source": "...",
      "dlssnr": { "current", "history": [ ... ] },                    nvngx_dlssnr.dll
      "lecram": { "current", "history": [ ... ], "generation" }       Lecram's DLSS5 add-on
    },
    "optiscalerCompat": {
      "updated": "...",
      "source": "...", "hudfixSource": "...",   the two OptiScaler wiki pages
      "games": [ ... ],              OptiScaler's compatibility list, three tables
      "hudfixIncompatible": [ ... ]  game names the HUDfix list calls incompatible
    },
    "addons":      { "updated", "source", "addons": [ ... ] },       packages from crosire's Addons.ini
    "frameLimiters": { "updated", "sources", "limiters": [ ... ] },  ReLimiter and Display Commander add-ons
    "luma":        { "updated", "sources", "release", "packages": [ ... ], "mods": [ ... ], "unrealGames": [ ... ] },  Luma Framework
    "shaderPacks": { "updated", "source", "packs":  [ ... ] }        shader repositories from shader-packs.json
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
recorded as a build). The one exception is a zip (it starts with `PK\x03\x04`), accepted only when its central directory lists an entry
named `renodx-dlss.addon64`. A file that fails keeps its last good entry, or is left out if it never had one.

### `addons`

Generated from crosire's list, `https://raw.githubusercontent.com/crosire/reshade-shaders/list/Addons.ini`: numbered sections with
`PackageName`, `PackageDescription`, `DownloadUrl` / `DownloadUrl32` / `DownloadUrl64`, `RepositoryUrl` and `EffectInstallPath`.
A section that is commented out (every line starts with `#`) is not part of the list. The list is read every run; fewer than 10
packages, or more than 20% fewer than before, fails the run and keeps the previous section.

An **add-on** entry:

| Field | Meaning |
|---|---|
| `id`, `name`, `author`, `description` | `id` is a slug of the whole package name; `name` and `author` are the package name split at " by " |
| `repositoryUrl`, `effectInstallPath` | As the list writes them (`effectInstallPath` is relative to the game folder) |
| `license` | SPDX id of the repository the files come from, when GitHub reports one |
| `managedBy` | The app already manages this add-on as a component of its own: `renodx`, `display-commander`, `relimiter`, `dfc`, `lecram-dlss5`, `gitc-uplift` or `dlss5-feeder` (matched on name, repository and download addresses). No files are listed; the app points at the component instead of installing a second copy |
| `manual` | Why the app can't install it itself: no download is published, the download is dead, or the zip holds files the app doesn't place (shader replacements, a launcher, ...). The repository link is the way in |
| `files` | The downloads, as in `renodx` files. `arch` is `x86`, `x64`, or `both` (one download listed for both, or a zip holding both builds) |

Every download is fetched, hashed and mirrored or pinned exactly as in `renodx` (below). A zip must be readable and hold at least
one file; a plain binary must start with `MZ`. A file entry of an add-on also has:

| Field | Meaning |
|---|---|
| `payload` | What the app installs from it: `{ "entry", "kind": "addon" or "effect", "arch", "dest" }`. `addon` = a `.addon32` / `.addon64` / `.addon` file for the add-on folder (a bare `.addon` takes the bitness of the list key it came from). `effect` = a `.fx` / `.fxh` file; `dest` is its path below the game's `Shaders` folder: after a `Shaders` folder inside the zip if there is one, else flat in `effectInstallPath` |
| `unplaced` | Zip entries that are neither documentation, an add-on nor a placeable effect. The package is then `manual` |

### `shaderPacks`

Generated from [`shader-packs.json`](shader-packs.json), the list we maintain by hand (not copied from anyone's list). Each pack:

```
{ "id": "lilium-hdr", "name": "...", "repo": "EndlesslyFlowering/ReShade_HDR_shaders",
  "source": { "kind": "branch", "branch": "master" }      or { "kind": "release-asset", "asset": "*.zip" },
  "category": "hdr|general|color|depth-3d|cinematic", "default": true, "license": "MIT" (optional), "description": "..." }
```

The generator pins each pack: a branch pack to the branch's current commit (`GET /repos/{repo}/commits/{branch}`, zip from
`https://github.com/{repo}/archive/{sha}.zip`, so the URL names exactly the bytes that were hashed); a release-asset pack to the latest
release's tag and its one asset matching the pattern. `version` is the release tag or the first seven characters of the commit.
The pack's `file` is a normal file entry (hash, size, history, `mirrorUrl` when the licence allows) with a `layout`:
`shadersDir` and `texturesDir` (zip prefixes, found without case and without stray spaces; the folder with the most `.fx` files wins),
the sorted file names of its effects (`effects`, because presets name effects by file name) and the file counts. A zip without a `Shaders` folder holding `.fx` files is refused like a bad download. `license` is GitHub's SPDX id; the
optional `license` in the list is a licence we read in the repository's licence file and is used only when GitHub reports none
(`NOASSERTION`), so a hand-checked MIT still counts as permissive. A conflict (the list says X, GitHub says Y) is a warning and GitHub wins.

Pre-selected packs (`default: true`) are the HDR essentials. The app never installs anything on its own: the flag only decides
what is ticked the first time.

### Mirror or pinned

If the source repository's licence is MIT, BSD-2-Clause, BSD-3-Clause, Apache-2.0, ISC or the Unlicense (GitHub API
`license.spdx_id`; `https://<owner>.github.io/<repo>/...` is looked up as `<owner>/<repo>`), the file is uploaded as an
immutable release asset of this repository and `mirrorUrl` points at it. Assets live in one release per month
(`mirror-2026-10`, by the month the build was first seen), are named `<first 16 hash characters>-<file name>`, and are
never overwritten or replaced. Any other licence, no licence, or a source we can't attribute to a repository: no copy is
made, the entry keeps the upstream `url` plus the hash, and the app refuses the file if upstream changes it before the
next run.

MIT, BSD and Apache require the copyright and licence notice to travel with copies, so the notes of every `mirror-YYYY-MM`
release carry them. After the file sync the generator groups every mirrored file (the current build and the history entries
that kept a `mirrorUrl`) by month and source repository, reads each repository's licence file once
(`GET /repos/{repo}/license`), and writes one block per repository, sorted by name: the `owner/repo` link, the SPDX id, a
link to the licence file, the `Copyright ...` lines of the licence text (or "Copyright holder: see the licence file at
<link>" when it has none) and the asset names. The full licence text is not copied. The release's first paragraph is
kept, the body is only written when it differs, and the text is the same for the same input.

```
### clshortfuse/renodx
- Repository: https://github.com/clshortfuse/renodx
- Licence: MIT (https://github.com/clshortfuse/renodx/blob/main/LICENSE)
- Copyright:
  - Copyright (c) 2024 Jane Doe
- Files:
  - 0123456789abcdef-renodx-example.addon64
```

If a licence or the release notes can't be read or written the run reports an error (exit code 1, retried on the next run)
but `catalog.json` is still written.

The mirrored files belong to their authors and stay under their licences. They are copies for reliability and hash
pinning, not a new distribution: see each file's `license` and the source repositories (for example
[clshortfuse/renodx](https://github.com/clshortfuse/renodx), [NotVoosh/renodx-unity](https://github.com/NotVoosh/renodx-unity),
[marat569/renodx](https://github.com/marat569/renodx)) for the licence texts and credits. The binaries of the `renodxDlss` and `neural`
sections are the exception: they are downloaded and installed automatically and are always mirrored (below).

### `renodxDlss`

ShortFuse's `renodx-dlss.addon64` has no official download channel. The builds are published as GitHub releases of
`RankFTW/rhi-repo`, tagged `renodx-dlss-SF-<version>` (for example `26.1003.2350`, older ones `0.55`). Each release has one
asset, a **zip** named `renodx-dlss_SF_<version>.zip` whose only entry is `renodx-dlss.addon64` at the root (about 1 MB).
The generator reads `https://api.github.com/repos/RankFTW/rhi-repo/releases?per_page=100`, keeps the releases with that tag
that are neither drafts nor prereleases and have exactly one `.zip` asset, and lists the newest 20 by publish date.

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
repository has no licence, but every build is **mirrored** all the same (assets of the monthly `mirror-YYYY-MM` release, no licence block in its notes), so `file.mirrorUrl` is always there. A reader downloads the
zip from `mirrorUrl` (else `url`), checks the hash, and takes `renodx-dlss.addon64` out of it. A zip without that entry is refused like a bad
download. If the release list is unreachable, isn't a list or has no matching release, the run fails and the previous
section stays; a single build whose download fails is a warning and keeps its last good entry.

A raw `renodx-dlss.addon64` in the `incoming` release (see `neural`) is mirrored as it is and listed as a build tagged
`incoming-<version>` (`version` is the first-seen version, `published` the time of the run); its `file.name` is
`renodx-dlss.addon64` instead of a zip and `url` equals `mirrorUrl`. Those builds stay in the list until they drop off the newest 20.

### `neural`

NVIDIA's `nvngx_dlssnr.dll` and Lecram's DLSS5 add-on builds, hosted in this repository's mirror as plain binaries (not zips).

```json
{
  "updated": "2026-10-21T12:00:00Z",
  "source": "https://api.github.com/repos/RankFTW/rhi-repo/releases?per_page=100",
  "dlssnr": {
    "current": { "name": "nvngx_dlssnr.dll", "version": "310.8.3", "sha256": "...", "size": 165840496, "firstSeen": "2026-10-21T12:00:00Z", "mirrorUrl": "https://github.com/.../mirror-2026-10/<hash16>-nvngx_dlssnr.dll", "url": "https://github.com/RankFTW/rhi-repo/releases/download/..." },
    "history": [ { "name": "nvngx_dlssnr.dll", "version": "310.8.0", "sha256": "...", "size": 165840496, "firstSeen": "...", "mirrorUrl": "..." } ]
  },
  "lecram": {
    "generation": "v10",
    "current": { "name": "renodx-dlss5.addon64", "version": "v8.5.0-rc10", "generation": "v10", "sha256": "...", "size": 3141632, "firstSeen": "...", "mirrorUrl": "..." },
    "history": [ ]
  }
}
```

Each entry has `name`, `version`, `sha256`, `size`, `firstSeen` and `mirrorUrl`; a reader downloads `mirrorUrl`, checks the hash and
size and saves the file as `name`. `url` (where the build was taken from, absent for a hand-dropped file) is provenance. `current` is
the newest build, `history` the older ones, newest first; together at most 20 per kind. Order: Lecram's v11 builds above v10, then
labelled builds above those whose only label is the DLSS SDK's (`v310.8.0`), then the version (numerically, a release above its release candidates), then `fileVersion` (numerically per dotted part, builds without one last), then first-seen time. Lecram v10 builds can share one `version` label; `fileVersion`, the PE file version as digits (`0.2026.828.517`), tells them apart. It is optional: entries listed before it existed get it on a later run, read from their mirror copy after the size and SHA-256 check (at most 10 files per run; a failed read leaves the entry as it is and logs a warning). A build is identified by its SHA-256, so
the same binary from two sources is listed once. `lecram.generation` is the generation of `current`; a build's own `generation` is
`v10` (`renodx-dlss5.addon64`, v10 and older) or `v11` (`dlss5-nr.addon64`).

Sources, in this order of work:

1. The releases of `RankFTW/rhi-repo`: tags `dlssnr-*` (zip with `nvngx_dlssnr.dll`) and `renodx-dlss5-*` / `dlss5-nr-*` (zip with the add-on).
   Only the facts of the release list are used. The zip must hold exactly one file; it is extracted, checked as a PE file, identified
   (below) and hashed, and the **binary** is uploaded. A release whose download url is already listed is not downloaded again.
2. The `incoming` release of this repository (a prerelease, created when missing). Raw files dropped there are identified by their
   content, mirrored like the rest and then deleted from `incoming`; an upload that failed leaves the file there. A file that is
   none of the known binaries stays in `incoming` and the run log has a warning. The same file twice (same hash) is a no-op.

Identification reads the PE version resource (without Windows APIs): `nvngx_dlssnr.dll` has the internal name `DLSSNR` and its
file version (`310,8,3,0` is `310.8.3`) is the version; Lecram v10 names itself `renodx-dlss5.addon64` in `OriginalFilename`, v11
is the file with the strings `RenoDX.DLSS5`, `NR-SETTINGS` and `NR-COMPAT`; both take the highest `v1.2.3[-pre]` string in the
binary as their version (the rule of the app's Lecram import). A Lecram build from an rhi-repo release takes its version from the tag
instead (`renodx-dlss5-7.0.0-rc8` is `v7.0.0-rc8`), also for builds listed earlier: older v10 binaries hold no label of their own,
only the DLSS SDK's `v310.8.0`. ShortFuse's `renodx-dlss.addon64` is recognised by its name.

Mirroring is needed: a run with `--no-mirror` (or without a token) leaves the `neural` section as it is.

### `optiscalerCompat`

The OptiScaler wiki's compatibility list and its "HUDfix incompatible" page, read as raw Markdown from
`https://raw.githubusercontent.com/wiki/optiscaler/OptiScaler/Compatibility-List.md` and `.../Hudfix-incompatible.md`. Nothing is
downloaded, hashed or mirrored. The wiki states no licence for its text, so only facts are copied (name, status, inputs, a short
plain-text note) and every entry points back to its upstream page through `page`.

```json
{
  "updated": "2026-10-21T12:00:00Z",
  "source": "https://raw.githubusercontent.com/wiki/optiscaler/OptiScaler/Compatibility-List.md",
  "hudfixSource": "https://raw.githubusercontent.com/wiki/optiscaler/OptiScaler/Hudfix-incompatible.md",
  "games": [
    { "name": "Cyberpunk 2077", "page": "Cyberpunk-2077", "list": "main", "compat": "working",
      "inputs": ["DLSS", "FSR3", "FSR3.1/4", "XeSS"], "optiPatcher": true, "notes": "..." }
  ],
  "hudfixIncompatible": ["Alan Wake 2", "Assassin's Creed: Mirage"]
}
```

| Field | Meaning |
|---|---|
| `name`, `page` | The game as the wiki writes it; `page` is the slug of its own wiki page (`https://github.com/optiscaler/OptiScaler/wiki/<page>`), absent when the wiki lists a plain name |
| `list` | The table the row is in: `main`, `upscalerMods` (games that only get upscalers through a third-party mod) or `lumaUe` (Luma for Unreal Engine) |
| `compat` | `working` (checkmark), `not-working` (cross) or `single-os` (works on Windows or Linux only; the note says which). Rows with any other mark are skipped with a warning |
| `inputs` | Upscaler inputs as written upstream (`DLSS`, `FSR2`, `FSR3`, `FSR3.1/4`, `XeSS`); empty when none is listed |
| `optiPatcher` | The wiki marks the game as supported by OptiPatcher. Only the `main` table has that column, so it is always `false` elsewhere |
| `notes` | Upstream's note as plain text: `<br>` becomes a space, links keep only their text. The images column is not copied |

Rows keep upstream order, lists come in `main`, `upscalerMods`, `lumaUe` order, and a repeated name within a list keeps its first row
(warning in the run log). If either page is unreachable, fewer than 100 `main` games parse, or fewer than 5 HUDfix names are
found, the run fails and the previous section stays.

### `frameLimiters`

The two frame-limiter add-ons DLSS5 Updater installs next to ReShade, read from their authors' GitHub releases:

| Limiter | Release read | Files | Licence |
|---|---|---|---|
| ReLimiter (`id` `relimiter`) | `https://api.github.com/repos/RankFTW/ReLimiter/releases/latest` | `relimiter.addon64`, `relimiter.addon32` (only releases from v3.3.1 on have the 32-bit file) | MIT: **mirrored** |
| Display Commander (`id` `display-commander`) | `https://api.github.com/repos/pmnoxx/display-commander/releases/tags/latest` | `zzz_display_commander.addon64`, `zzz_display_commander.addon32` | none found (the README names a LICENSE file the repository does not have): **pinned**, never mirrored |

Each limiter entry has `id`, `name`, `repository`, `release` (the tag the files came from; Display Commander republishes `latest` on every
push to its main branch, so the hash changes while the tag does not), `license` (SPDX, absent when none) and `files`, normal file entries
with hash, size, first-seen version and history. The 32-bit file is simply absent when the release has none; a reader must refuse a 32-bit
game then. A release without the 64-bit file is an error for that limiter (its last good entry stays and the run fails). The debug symbol
files of Display Commander's release are not listed. The ids are the same as in the `addons` section's `managedBy`.

### `luma`

The Luma Framework (`Filoppi/Luma-Framework`), read from its wiki home page (`https://raw.githubusercontent.com/wiki/Filoppi/Luma-Framework/Home.md`) and the zips
of its latest release (`https://api.github.com/repos/Filoppi/Luma-Framework/releases/latest`). The repository's licence is NOASSERTION, so every zip is
**pinned** (the upstream URL plus the hash), never mirrored.

| Field | What it is |
|---|---|
| `release` | The release tag the zips were read from (`latest-755`). It is part of every file URL, so a new tag is a new address |
| `packages` | One per release zip that is needed: `id` is the asset name without `Luma-`, the bitness and `.zip` (`Prey`, `BioShock_Series`, `Unity_Engine`, `Unreal_Engine`). `files` has `x64` (`Luma-<id>.zip`) and/or `x86` (`Luma-<id>-x32.zip`), normal file entries with hash and history. `-Test` and `-Dev` builds, `Generic_Mod` and `Graphics_Analyzer` are not listed |
| `mods` | The wiki's "Completed Mods" rows: `name`, `authors`, `status` (`working` or `in-progress`), `notes` (plain text), `links` (Nexus Mods, GameBanana, other), `featuresUrl`, `requiresDgVoodoo` with `dgVoodooVersions` and `dgVoodooSetup` (the wiki's `dgVoodoo.conf` lines). `package` is the zip the wiki links, or the release zip with the same normalised name; absent when the mod is only published elsewhere. Release zips the wiki doesn't list are added without `status` |
| `unrealPackage` | The package that is the generic Unreal Engine mod (`Unreal_Engine`) |
| `unrealGames` | The wiki's generic Unreal Engine table: `dlss` and `hdr` (`yes`, `blocked`, `in-progress`, absent when empty), `ueVersion`, `notes`, `engineIni` (section, key, value from the "Modify Engine.ini" blocks; the section is `SystemSettings` when the block names none) and `launchArgs` (`-dx11` style arguments the note names) |

If the wiki has fewer than 10 mods or 50 Unreal games, or the release has no `Luma-Unreal_Engine.zip`, the run fails and the previous section stays.
A dead zip link is a warning and keeps the last good entry. A wiki link to a zip the release doesn't have is a warning and the mod has no `package`.

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
generator, and commits `catalog.json` if it changed. Pull requests and pushes run the tests only. A run started with
`workflow_dispatch` (for example by a hook after a file landed in the `incoming` release) picks up the dropped files.

Locally (.NET 8 SDK):

```
dotnet test
dotnet run --project src/CatalogGen -- --no-mirror          # hashes and pins, uploads nothing
```

`--catalog`, `--overrides`, `--packs`, `--temp` and `--repo` change the paths; `GITHUB_TOKEN` raises the API rate limit and, with
`--repo`, enables mirror uploads. `CATALOG_ACCEPT_SHRINK=1` accepts a section that lost more than 20% of its mods.

## Layout

```
catalog.json              generated, committed by the Action
overrides.json            hand-maintained
shader-packs.json         hand-maintained list of shader repositories
schema/catalog.schema.json
src/CatalogGen            the generator (.NET 8 console app)
tests/CatalogGen.Tests    xUnit: wiki parsing, merge, licence decision, history, mirror, schema
.github/workflows/catalog.yml
```

The generator code is MIT (see [LICENSE](LICENSE)); the mirrored files are not part of it.
