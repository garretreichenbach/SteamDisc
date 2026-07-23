# Project Plan — Steam Physical Media Installer Suite

**Working name:** SteamDisc
**Status:** M1–M4, M6, M7 and the Cover Studio implemented; S1 still unverified
**Stack:** C# / .NET 8 (LTS), Windows-first

> **Implementation notes.** This document is the design; [README.md](README.md) describes what
> exists. Where building it taught us something the plan got wrong, the plan has been corrected
> in place and the correction called out in a note like this one.

---

## 1. Goals

Build a two-part toolchain that turns owned Steam games into authentic-feeling physical media:

1. **Authoring tool** — pick an installed game, fetch or supply cover art, choose a skin, package the payload, emit a burnable ISO (or burn directly).
2. **Disc runtime** — a self-contained, skinned installer that runs from the disc, restores the game into a Steam library, registers it with Steam, and launches it.

### Non-goals

- Acquiring games the user doesn't own. The runtime assumes the target Steam account owns the app; Steam enforces this regardless.
- Cross-platform disc runtime at v1. Linux/Steam Deck is a possible v2 (Avalonia already leaves the door open).
- Replacing Steam. The suite manipulates Steam's local state; Steam remains the owner of licensing, updates and launch.

### Success criterion for v1

Burn *Portal 2* (or similar, ~15 GB, no third-party DRM) to a single BD-R. Insert into a clean Windows machine with Steam installed and the account logged in. AutoPlay → skinned installer → install completes → game appears in the Steam library as fully installed → launches. Total network traffic during install: near zero.

---

## 2. Install mechanism — the core decision

Three viable paths. **v1 targets Path B**, with Path A retained as a fallback mode.

### Path A — Drive Steam's native restore

Point Steam at a folder containing `sku.sis` via `steam.exe -install <path>`, or open the restore wizard through the `steam://` protocol.

- **Pro:** Officially sanctioned flow. Steam handles depot registration correctly.
- **Con:** The `.csd`/`.csm` container is proprietary and undocumented — you cannot author, inspect, repair, or recompress it outside Steam. Historically flaky (hangs, silent exits). Multi-disc sequencing has known bugs requiring `sku.sis` field surgery. Always contacts Steam.

### Path B — Own archive + appmanifest injection ← **chosen**

Ship the raw `steamapps/common/<installdir>` tree in our own archive format. Extract to the chosen library, then write a correct `appmanifest_<appid>.acf` into `steamapps/`. Restart or nudge Steam; the game appears installed.

- **Pro:** We own the format end to end — compression choice, splitting, integrity hashing, progress reporting, resume. Debuggable. No reverse engineering of a proprietary blob.
- **Con:** ACF fidelity is the whole ballgame. A wrong `buildid` or missing depot manifest ID and Steam decides an update is needed and pulls the entire game down, defeating the point. Mitigation: capture the *real* ACF at authoring time from the source machine and transplant it, rather than synthesising one.

### Path C — Hybrid

Extract, write ACF with `StateFlags = 1026` (update pending), then fire `steam://validate/<appid>` so Steam verifies locally. Slower, but self-healing when the ACF is imperfect. Ship as an "Advanced → Verify after install" checkbox.

**StateFlags reference (community-documented, verify in spike S1):**
`4` = fully installed · `1026` = update required/queued · `6` = update running

> **Correction from implementation.** These are *bit flags*, not opaque constants, and one of
> the values above was wrong. `4` = FullyInstalled; `6` = FullyInstalled | UpdateRequired
> (installed but flagged for a check — not "update running"); `1026` = UpdateStarted |
> UpdateRequired. Modelled as `AppStateFlags` in Core, which makes Path C's "installed, please
> verify" state expressible rather than magic.

---

## 3. Solution architecture

```
SteamDisc.sln
├── SteamDisc.Core            (netstandard2.1 / net8.0) — shared, no UI
│   ├── Steam/                  locator, libraryfolders.vdf, ACF model
│   ├── Vdf/                    KeyValues reader + writer (text & binary)
│   ├── Payload/                payload.json model, versioning, hashing
│   ├── Archive/                7z / zstd wrapper, split volumes, progress
│   ├── Theming/                theme.json model, asset resolution
│   └── Protocol/               steam:// driver, steam.exe CLI driver
│
├── SteamDisc.Art             (net8.0) — art acquisition, used by Builder only
│   ├── Providers/              Steam CDN, SteamGridDB, IGDB, local/custom
│   └── Cache/                  disk cache, dedup by content hash
│
├── SteamDisc.Builder         (net8.0, WPF or Avalonia) — the authoring app
│
├── SteamDisc.Runtime         (net8.0, WinForms or Avalonia) — the disc installer
│                               self-contained, single-file, trimmed
│
├── SteamDisc.Imaging         (net8.0) — UDF/ISO generation, IMAPI2 burning
│
└── SteamDisc.Tests
```

> **As built.** The tree above is close, with the engines split out so both front-ends share
> them: `SteamDisc.Install` (the install engine), `SteamDisc.Authoring` (the packaging engine)
> and `SteamDisc.Covers` (print geometry and PDF). `Builder` and `Runtime` are command-line
> front-ends over those engines; the skinned UI is still to come and adds a view, not logic.

### Why Core is UI-free

The Builder needs the ACF reader; the Runtime needs the ACF writer. Both need VDF and archive handling. Keeping Core headless also gives a free CLI surface for batch authoring later.

> **As built.** This paid off immediately: the entire pipeline is exercised by the test suite
> and by a CLI, with no UI in the loop. It also answers open question 3 — the CLI is the batch
> surface, and it exists.

### Runtime size constraint

The disc runtime must run on a machine with no .NET installed. Options:

| Option | Size | Notes |
|---|---|---|
| WinForms, self-contained, trimmed | ~35–45 MB | Ugly by default, but fully skinnable if we draw custom |
| WPF, self-contained, trimmed | ~60–80 MB | Best styling story; **no** Native AOT |
| Avalonia, self-contained, trimmed | ~45–60 MB | Cross-platform path, AOT-capable |

On a 25 GB disc none of these matter. **Recommendation: Avalonia** — the styling system is the best fit for a skin engine, and it preserves the Linux/Deck option.

---

## 4. Disc layout & payload format

```
D:\
├── autorun.inf
├── Setup.exe                  ← SteamDisc.Runtime, self-contained
├── payload.json               ← manifest (see below)
├── appmanifest_620.acf        ← transplanted from source machine
├── theme/
│   ├── theme.json
│   ├── background.png
│   ├── logo.png
│   ├── cover.png
│   └── install.wav
└── data/
    ├── payload.7z.001
    ├── payload.7z.002
    └── payload.sha256
```

### payload.json (draft)

```json
{
  "formatVersion": 1,
  "title": "Portal 2",
  "appId": 620,
  "installDir": "Portal 2",
  "buildId": 12345678,
  "sizeOnDisk": 15234567890,
  "createdUtc": "2026-07-23T00:00:00Z",
  "disc": { "number": 1, "of": 1, "setId": "guid" },
  "archive": { "format": "sdz", "volumes": 2, "hashFile": "data/payload.sha256" },
  "prerequisites": [
    { "name": "VC++ 2015-2022 x64", "path": "_CommonRedist/vcredist/...", "args": "/quiet /norestart" }
  ],
  "postInstall": { "validate": false, "launch": true }
}
```

> **Correction from implementation.** The payload format is `sdz`, SteamDisc's own container,
> not 7-Zip. Shelling out to `7z` on a machine that has just booted from optical media is
> exactly the external dependency Path B exists to avoid, and "we own the format end to end"
> only means something if we actually do. `.sdz` is written straight through with no seeking
> (it spans volumes and discs), frames file data as independent chunks (a scratch costs one
> chunk, and the reader can name the file and offset that failed), and carries a per-file
> SHA-256. 7-Zip remains available as `--format 7z` for the better ratio, single-disc only.
> The real `payload.json` also carries a per-volume list with sizes, hashes and disc numbers,
> plus authoring advisories.

### Multi-disc

BD-R is 25 GB (BD-R DL 50 GB, BD-R XL 100 GB). Plenty of modern titles exceed all of these, so **disc spanning is a v1 feature, not a v2 nicety.** Design: split volumes carry a `setId` GUID and sequence number; the runtime prompts for the next disc, verifies `setId` matches, and resumes. Learn from Steam's own bug here — the failure mode where only disc 1 is read and the rest is silently re-downloaded is exactly what we're avoiding.

---

## 5. Skin / theme system

The point of the project is that a burned disc feels like a retail product. The skin engine is therefore a first-class component, not a coat of paint.

### theme.json (draft)

```json
{
  "name": "Valve Retail 2011",
  "layout": "classic-splash",
  "colors": { "accent": "#FF6600", "bg": "#101014", "text": "#EAEAEA" },
  "fonts": { "heading": "fonts/Motiva.ttf", "body": "system" },
  "assets": {
    "background": "background.png",
    "logo": "logo.png",
    "cover": "cover.png"
  },
  "audio": { "onLaunch": "intro.wav", "onComplete": "done.wav" },
  "strings": { "installButton": "Install", "playButton": "Play" }
}
```

### Built-in layouts to ship

1. **Classic Splash** — full-bleed key art, logo, Install/Play/Exit. The 2000s retail look.
2. **Modern Card** — cover art card, progress ring, minimal chrome.
3. **Multi-Game Menu** — for compilation discs; grid of covers, per-title install state.

Themes live as loose folders and are packed into the disc root at build time, so a user can hand-edit a burned disc's theme without rebuilding. Builder ships a live preview pane.

---

## 6. Art tooling

`SteamDisc.Art` exposes `IArtProvider` with a common `SearchAsync` / `FetchAsync` surface, so the Builder's art picker is provider-agnostic.

### Providers

**Steam CDN (no key required)** — canonical first-party art. Conventional paths under
`https://cdn.cloudflare.steamstatic.com/steam/apps/{appid}/`:
`header.jpg` (460×215) · `capsule_616x353.jpg` · `library_600x900.jpg` · `library_hero.jpg` · `logo.png`
Newer titles have migrated some assets to `shared.akamai.steamstatic.com/store_item_assets/steam/apps/{appid}/`. **Path drift is real — this belongs in the spike list (S4)**, with graceful fallback rather than hard-coded assumptions.

**SteamGridDB** — the community art database, keyed by Steam AppID among others. Serves grids, heroes, logos and icons. API v2 at `https://www.steamgriddb.com/api/v2`; a personal API key is generated from account Preferences → API. There's an existing C# client, **craftersmine/SteamGridDB.NET**, targeting v2 — worth adopting rather than rolling our own HTTP layer. Note the maintainer flags v3 as planned with no ETA, so keep the provider abstraction thin enough to swap.

**IGDB** — broader metadata and cover art, useful for non-Steam or obscure titles. Twitch OAuth client id + secret. Lower priority; add once the provider interface is proven with two implementations.

**Local / custom** — drag-and-drop, clipboard paste, and a folder watcher. Must be as fast as the online providers, since power users will mostly bring their own art.

### Art pipeline

Fetch → cache by content hash → auto-crop/letterbox to the aspect the layout wants → optional effects (vignette, blur-extend for backgrounds) → embed. Keep an `art.json` sidecar recording provider and source URL so a rebuild is reproducible.

### Reference implementations worth reading

- `boppreh/steamgrid` (Go) — mature multi-source resolution logic, including preference ordering across static/animated styles and per-asset skip flags. Good prior art for the fallback chain.
- LaunchBox's SteamGridDB scraper plugin — good UX model for the "close matches" disambiguation dialog when title matching is fuzzy.

---

## 7. Prior art

Nothing does the whole job, but several pieces exist and are worth reading before writing equivalent code.

| Project | Language | Relevance |
|---|---|---|
| **SteamDeploy** (ShedoSurashu) | C# | Closest existing runtime — autorun.inf + one-click trigger of Steam's restore. Thin wrapper; no skinning, no own payload format. |
| **steamgamecovers.com `Setup.exe`** | — | Community clone of the retail Steam disc installer, skinnable via config/images/sounds, plus a multi-game prelauncher. Design reference for layouts; closed and dated. |
| **Steam Backup Tool** (Du-z, fork by j-oliveras) | C#/.NET 4.0 | Closest existing builder — 7-Zip compression, CLI for scheduling, incremental backup of changed games, restore to alternate library, auto-install after restore. Ancient but the architecture is instructive. |
| **DepotDownloader / SteamKit2** (SteamRE) | C# | Reference implementation for Steam's content system. Primary source for appinfo, build IDs and depot manifest IDs — directly relevant to ACF fidelity. |
| **craftersmine/SteamGridDB.NET** | C# | Drop-in art provider. |

---

## 8. Spike list — resolve before committing to milestones

These are the genuine unknowns. Each is a timeboxed throwaway experiment.

- **S1 — ACF fidelity (highest risk).** Install a game, capture its ACF, uninstall, wipe the folder, restore from a plain 7z + transplanted ACF, restart Steam. Measure: does Steam report installed, and how many bytes does it download? Repeat with a stale `buildid` to characterise the failure mode. *Everything else depends on this working.*
- **S2 — Steam nudge without restart.** Determine whether `steam://validate/<appid>`, an appinfo refresh, or anything short of a client restart makes Steam pick up a newly dropped ACF. Affects UX significantly.
- **S3 — AutoPlay behaviour.** Confirm current Windows behaviour for `autorun.inf` on optical media (expect an AutoPlay *prompt* rather than silent execution — silent autorun was killed for removable media years ago, optical is a prompt). Design the UX around a prompt, not around auto-execution.
- **S4 — Steam CDN art paths.** Sample 30 AppIDs across release years; map which assets live at which host/path. Build the fallback chain from evidence.
  *Partly answered.* The provider probes a candidate list per asset kind across three hosts and
  takes the first that responds, so a path that disappears costs a fallback rather than a
  feature. Two findings worth recording: the nominal size a path implies is not reliable
  (Portal 2 serves a 600×900 image from its `_2x` path, so dimensions are read from the bytes
  after fetching), and **Steam's library art is not print resolution** — 600×900 is about
  116 DPI across a DVD panel. For covers, SteamGridDB or the user's own files are needed, and
  the renderer says so rather than letting it be discovered after printing.
- **S5 — IMAPI2 from C#.** Burn a >25 GB UDF image to BD-R DL via COM interop. Decide burn-direct vs. emit-ISO-and-delegate-to-ImgBurn.
  *Resolved by implementation: delegate.* `isoburn.exe` ships on every supported Windows,
  understands BD-R, and its dialog already handles media detection, speed and verification.
  Driving IMAPI2 directly only buys progress inside our own window, which is not worth blocking
  M4 on. Also resolved: **UDF is not needed.** Its reason to exist here would be files of 4 GB
  and over, and capping payload volumes below that limit keeps us on ISO 9660 + Joliet, which
  every OS and drive can read. The image writer is ours, and is verified in the test suite by
  an independently written ISO reader.
- **S6 — Runtime footprint.** Build the Avalonia shell self-contained + trimmed; confirm it launches from optical media on a machine with no .NET runtime, and measure cold-start off BD-R.

---

## 9. Milestones

> **Status.** M1–M4, M6 and M7 are implemented and tested; M5's model exists but its skinned UI
> does not. The Cover Studio (section 12) was added during implementation. M0 remains open: S1
> needs a real Windows machine with Steam and is the one thing that cannot be settled here.

**M0 — Spikes (S1–S6).** Gate: S1 passes, or the project pivots to Path A. *(S3–S5 resolved; S1, S2, S6 need real hardware.)*

**M1 — Core library.** *(Done.)* Steam locator, `libraryfolders.vdf` parsing, VDF read/write, ACF model round-trip, archive wrapper with progress and split volumes. Unit tested against real Steam installs. No UI.

**M2 — Runtime, unskinned.** *(Done — console front-end.)* Console or bare-window installer. Reads `payload.json`, extracts, runs `_CommonRedist` prerequisites, writes ACF, invokes `steam://run/<appid>`. Run from a folder, not a disc. Gate: the v1 success criterion, minus the disc.

**M3 — Builder, minimal.** *(Done — CLI.)* Enumerate installed games, select one, package to a folder with correct `payload.json` and transplanted ACF. Pairs with M2 for the first true end-to-end.

**M4 — Imaging.** *(Done — ISO 9660 + Joliet writer and burn hand-off.)* ISO generation, `autorun.inf`, burn or hand-off. Gate: the full v1 success criterion, on real media.

**M5 — Theme engine.** *(Model and string system done; layouts need the skinned UI.)* `theme.json`, the three built-in layouts, Builder live preview.

**M6 — Art tooling.** *(Done — providers, cache, sidecar, ranking.)* Steam CDN + SteamGridDB providers, art picker UI, cache, crop pipeline, custom art import.

**M7 — Multi-disc.** *(Done — spanning, set verification, swap prompts, all covered by tests.)* Spanning, `setId` verification, disc-swap prompt, resume.

**M8 — Polish.** Code signing (SmartScreen will flag an unsigned installer), error recovery, logging, disc label/insert artwork export as a bonus for the physical-media angle.

---

## 10. Known constraints to design around

- **Ownership.** Steam checks that the account owns the app. Offline mode works only if the client has previously authenticated on that machine. The disc is a *content* delivery mechanism, not a licence.
- **Third-party DRM.** Denuvo, EA, Ubisoft Connect and similar titles will phone home on first launch regardless. Flag these in the Builder with a warning rather than pretending otherwise.
- **Games that patch constantly.** Live-service and multiplayer titles will download a delta immediately. Best suited: single-player titles, ideally ones that have stopped updating. Consider surfacing "last update" from appinfo in the Builder as a suitability hint.
- **Optical read speed.** BD-R at 6× is roughly 27 MB/s. A 40 GB install is ~25 minutes at best, before decompression. Progress UI must be honest and the estimate must not lie.
- **Disc rot.** Consumer BD-R is not archival. Worth a line in the docs; M-DISC exists if the goal is genuine long-term preservation rather than the retail-box experience.

---

## 11. Open questions

1. Single-title discs only at v1, or is the compilation/multi-game menu in scope earlier? (Affects the theme engine's shape.)
   *Still open.* The payload format carries one app, and the `MultiGameMenu` layout is defined
   but unused. Compilation discs would need a payload holding several apps — a format change,
   so worth deciding before v1 rather than after.
2. Should the Builder ever fetch depot data from Steam directly via SteamKit2, or stay strictly local-library-only? Local-only is simpler and cleaner in intent.
   *Answered by implementation: local-only, and it is sufficient.* Everything needed for a
   faithful transplant — build id, depot manifest ids, sizes — is already in the local ACF.
   `inspect` audits it without touching the network. No reason to add SteamKit2.
3. Is a CLI for the Builder wanted for batch-authoring a shelf's worth of discs, or is GUI-only fine?
   *Answered: yes, and it is the primary surface.* The CLI came first and the GUI will sit on
   the same engines.
4. Repo public or private? If public, the README framing matters — this is a personal-archival tool for games you own, and it should read that way.
   *Framing done; the public/private call is still yours.* The README opens by stating what the
   tool is and is not.
5. *(New.)* How far should cover template support go? Geometry for the common layouts is
   implemented and designs are imported rather than bundled. The open part is guide removal:
   a blank template sheet carries alignment guides an artist would erase by hand, and
   compositing preserves them.

---

## 12. Cover printing (added during implementation)

A burned disc in a paper sleeve is not the retail-box experience section 1 asks for, so cover
printing became a first-class component rather than the M8 afterthought it started as.

### What ships

**Geometry, not artwork.** Page size, trim box, spine width and slot positions for the common
layouts — DVD (Amaray) case, Blu-ray case, jewel case front and tray card, disc label, and both
case inserts — measured from published 300 DPI sheets rather than guessed. DVD is the default,
since that is the format most existing community covers use.

Designs are **imported, not downloaded**. The user fetches a template pack themselves; the tool
recognises the layout, applies the matching geometry, records the source, and composites the
design *over* the user's key art. That ordering is what keeps a template's branding and legal
text intact, which its terms generally require.

### Output

PDF, because it is the only common format carrying real-world dimensions: a print shop gets
272 mm, not pixels at an assumed DPI. The writer is dependency-free and never resamples the
user's art — JPEGs are embedded verbatim as `DCTDecode`, and a PNG's own zlib stream is replayed
through PDF's PNG predictor. CMYK JPEGs, common in print-oriented downloads, pass through
natively with a Decode array rather than being converted.

### Two workflows

- **Compose.** Art per slot, design on top, spine text substituted. Art comes from the same
  providers the theme engine uses.
- **Print a finished cover.** The community gallery is full of completed covers where the art is
  already part of the design. Those need no layout — only correct physical size — so they are
  recognised by sheet dimensions and printed 1:1.

### Known limits

- Blank template sheets carry alignment guides and instructional text that an artist erases by
  hand before finalising. Compositing preserves them.
- Steam's own library art is roughly 116 DPI across a DVD panel. It is fine on screen and soft
  on paper; the renderer warns rather than letting that be discovered after printing. SteamGridDB
  or user-supplied files are the answer for print.
