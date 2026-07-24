# SteamDisc

Turn a Steam game you own into physical media: a burnable disc that installs the game into a
Steam library, registers it with the client, and launches it — plus a print-ready cover for the
case and the disc face.

This is a personal-archival tool for games you already own. It does not acquire games, does not
bypass anything, and does not replace Steam: Steam still owns licensing, updates and launch. The
disc is a *content* delivery mechanism, not a licence.

## Status

The engine layer is complete and tested end to end. Both command-line and skinned **Avalonia GUI**
front-ends now exist. See [Project Plan.md](Project%20Plan.md) for the design and the remaining
milestones.

| Milestone | State |
|---|---|
| M1 Core library — Steam locator, VDF, ACF model, archive, spanning | Done |
| M2 Runtime — extract, prerequisites, manifest injection, launch | Done (console + skinned GUI) |
| M3 Builder — enumerate, package, transplant manifest | Done (CLI + GUI) |
| M4 Imaging — ISO 9660 + Joliet, autorun, burn hand-off | Done |
| M5 Theme engine — `theme.json`, layouts, strings | Done — model + skinned Avalonia UI |
| M6 Art tooling — Steam CDN, SteamGridDB, local, cache | Done |
| M7 Multi-disc — spanning, set verification, swap prompts | Done |
| Cover Studio — templates, import, print-ready PDF | Done |
| Skinned Avalonia front-ends | Done — authoring GUI + skinned installer |

The unverified part is the one the plan flags as highest risk: **spike S1**, whether a real
Steam client accepts a transplanted manifest without re-downloading. Everything here is built so
that experiment is cheap to run — see [Verifying the risky part](#verifying-the-risky-part).

## Quick start

Requires the [.NET 8 SDK](https://dotnet.microsoft.com/download/dotnet/8.0).

```bash
dotnet build SteamDisc.sln
```

List what you have installed, with a hint at how well each suits physical media:

```bash
dotnet run --project src/SteamDisc.Builder -- list
```

Check a game before committing to a disc — this reports the depots, the redistributables, and
whether its manifest is one Steam will accept:

```bash
dotnet run --project src/SteamDisc.Builder -- inspect 620
```

Package it:

```bash
dotnet run --project src/SteamDisc.Builder -- package 620 --media bd-r --out ~/discs/portal2
```

Install it back — from the staging folder, no burning required:

```bash
dotnet run --project src/SteamDisc.Runtime -- ~/discs/portal2/disc
```

Then make an image and burn it:

```bash
dotnet run --project src/SteamDisc.Builder -- iso ~/discs/portal2/disc --out ~/discs/portal2.iso
```

### Or use the GUIs

The same engines drive two Avalonia front-ends. The CLI stays the batch surface and the fallback;
the GUIs are a view over exactly the same code.

**Authoring GUI** — pick a game, choose media/compression/theme, attach artwork per package slot
(one click fetches Valve's own art from the Steam CDN, or upload your own — there is no built-in
image editor), watch a live preview of the disc's installer, then run the pipeline to a staging
folder, an ISO, and a burn:

```bash
dotnet run --project src/SteamDisc.Builder.App
```

**Skinned installer** — the graphical `Setup.exe` that ships in the disc root. It reads the disc's
`theme.json` and artwork and renders a per-game skin (Classic retail splash or Modern card) over
the same install engine. Publish it, and point the authoring GUI at it so it can be stamped onto
each disc:

```bash
dotnet publish src/SteamDisc.Runtime.App -c Release -r win-x64
# then, to preview it against a staging folder:
src/SteamDisc.Runtime.App/bin/Release/net8.0/win-x64/publish/Setup.exe ~/discs/portal2/disc
```

## How it works

The plan lays out three ways to get a game onto a disc and back off it. This implements **Path
B**: ship the raw game folder in our own archive, then write a correct `appmanifest_<appid>.acf`
into the target library so Steam believes the game is already installed.

The whole approach lives or dies on manifest fidelity. A wrong `buildid` or a missing depot
manifest id and Steam decides an update is due and downloads the entire game, which defeats the
point. So the manifest is **transplanted**, not synthesised: the real one is captured from the
authoring machine and only the fields that describe *this machine* or *this install event* are
rewritten.

| Rewritten | Left exactly as found |
|---|---|
| `LastOwner` — the installing account | `buildid` |
| `LauncherPath` | `InstalledDepots` and every manifest id |
| `StateFlags`, `LastUpdated` | `installdir` |
| Every byte counter, `UpdateResult`, `ScheduledAutoUpdate` | depot sizes |
| `TargetBuildID`, aligned to `buildid` | everything we do not understand |

That last row matters: the ACF model is a facade over a parsed KeyValues tree rather than a set
of fields, so keys Valve adds in future ride along untouched instead of being dropped.

`inspect` runs the same audit the installer does and tells you up front whether a game's
manifest will survive the trip.

### Ordering

Files land first; the manifest is written last. If anything fails partway you are left with a
stray folder, not a library entry pointing at an incomplete install — which Steam would "repair"
by downloading all of it.

### The archive

`.sdz` is our own container, documented in
[`SdzFormat.cs`](src/SteamDisc.Core/Archive/SdzFormat.cs). It exists because the disc runtime has
to unpack on a machine with nothing installed, and because owning the format end to end is the
entire argument for Path B.

- Written straight through with no seeking, because it is split across volumes that may end up
  on different discs.
- File data is framed as independent chunks, so a scratch costs one chunk and the reader can say
  which file and offset went bad rather than "archive corrupt".
- Per-file SHA-256, verified as it extracts.
- Compression is decided per chunk, which suits game data that is already mostly packed.

7-Zip is available as an alternate format (`--format 7z`) for a better ratio, at the cost of
needing `7z` present at install time. Single-disc sets only.

### Multi-disc

Volumes carry a `setId` and a disc number. When the reader runs off the end of a volume it asks
for the next one, and an optical source takes that as its cue to prompt for a swap. Every
swapped-in disc is checked against the set before a byte is read from it — the failure mode
where later discs are silently ignored and the content re-downloaded is exactly what this
avoids.

### Imaging

ISO 9660 with a Joliet tree, written directly rather than shelled out. Not UDF: UDF's reason to
exist here would be files of 4 GB and over, and the payload already splits below that limit, so
ISO 9660 + Joliet is readable everywhere and is the better trade. Images are verified in the
test suite by an independently written reader, and mount correctly on macOS.

## Cover Studio

A burned disc in a paper sleeve is not the retail-box experience the project is aiming at, so
covers are a first-class part of the tool rather than an afterthought.

```bash
# What layouts are available
dotnet run --project src/SteamDisc.Builder -- covers templates

# Import designs you downloaded (see below)
dotnet run --project src/SteamDisc.Builder -- covers import "~/Downloads/SGC_AMARY_2018 (PNG)"

# Compose a cover: your art underneath, the design on top
dotnet run --project src/SteamDisc.Builder -- covers new --disc ~/discs/portal2/disc --template sgc-dvd
dotnet run --project src/SteamDisc.Builder -- covers render cover.json

# Or print one somebody else already finished
dotnet run --project src/SteamDisc.Builder -- covers print ~/Downloads/portal2-cover.png
```

Output is PDF, because it is the only common format that carries real-world dimensions — a print
shop gets 272 mm, not "some pixels at some assumed DPI". The renderer is dependency-free: JPEGs
are embedded verbatim as `DCTDecode`, and a PNG's own zlib stream is replayed through PDF's PNG
predictor, so your artwork is never resampled or re-encoded on the way in. CMYK JPEGs, which
print-oriented downloads often are, pass through natively.

### On template artwork

The tool ships **geometry, not artwork**: page size, trim box, spine width and slot positions,
measured from the published 300 DPI sheets. It does not download or bundle anybody's designs.

You download a template pack yourself and import it. SteamDisc recognises the layout, applies
the matching geometry, records where it came from, and composites the design **over** your key
art — which is what keeps the Steam header and the website and legal text intact, as those
templates' terms require.

Recognised layouts: DVD (Amaray) case, Blu-ray case, jewel case front and tray card, disc label,
and both case inserts. DVD is the default, since that is what most existing community covers use.

One caveat worth knowing before you print: a blank template sheet also carries dashed alignment
guides and instructional text ("type spine text in this direction"), which an artist erases by
hand before finalising a cover. Compositing preserves the whole design, guides included. Erase
them in an image editor and re-import for a clean print — or use `covers print`, which takes a
cover somebody has already finished and has none of this problem.

### Print quality

Covers are printed at 300 DPI. Steam's own library art tops out around 600×900, which is only
about 116 DPI across a DVD panel — good enough on screen, visibly soft on paper. The renderer
says so rather than letting you find out after printing. For print, prefer SteamGridDB or your
own files:

```bash
dotnet run --project src/SteamDisc.Builder -- covers new --game 620 --steamgriddb-key <key>
dotnet run --project src/SteamDisc.Builder -- covers new --game 620 --art-dir ~/my-art
```

Print at 100% scale. Letting a print dialog "fit to page" will produce a cover that does not fit
the case.

## First run: prove it on a DVD-R

Do the cheap steps in order. Each one can fail on its own, and only the last costs a disc.

**1. Pick a candidate that fits.** This assumes no compression, which is the honest assumption —
game assets are already packed, so expect a few percent, not a few gigabytes.

```bash
dotnet run --project src/SteamDisc.Builder -- list --media dvd
```

**2. Check its manifest before spending any time on it.** A clean audit is the difference between
a disc that installs offline and one that makes Steam re-download the game.

```bash
dotnet run --project src/SteamDisc.Builder -- inspect <appid>
```

**3. Package it.** Publish the runtime first so the disc is self-contained.

```bash
dotnet publish src/SteamDisc.Runtime -c Release -r win-x64 -o publish/runtime
```

```bash
dotnet run --project src/SteamDisc.Builder -- package <appid> --media dvd --out D:\test --runtime publish/runtime/Setup.exe
```

**4. Install from the staging folder — no disc needed.** This is the real test of the risky part.
Uninstall the game in Steam first, and delete the folder if Steam leaves it behind.

```bash
D:\test\disc\Setup.exe
```

Restart Steam and watch the download counter. **Zero bytes means it worked.** If Steam starts
downloading, the manifest was not faithful enough — `inspect` and the installer's warnings should
name the field.

**5. Build the image and check it fits before burning.** `--media dvd` refuses rather than
producing an oversized ISO.

```bash
dotnet run --project src/SteamDisc.Builder -- iso D:\test\disc --out D:\test\game.iso --media dvd
```

Mount the ISO and run `Setup.exe` from the mounted drive. That exercises everything the burned
disc will do, including reading over a filesystem rather than a folder.

**6. Only now burn it.**

```bash
dotnet run --project src/SteamDisc.Builder -- burn D:\test\game.iso
```

Notes for a DVD-R test specifically:

- `--compression maximum` is worth trying if a game is just over the line. It is much slower and
  usually gains little, but "little" can be the difference between one disc and two.
- If the game needs two discs, that path works and is tested, but do the folder install at step 4
  first — a swap prompt failure after two burns is an expensive way to find a bug.
- AutoPlay gives a *prompt*, not automatic execution. That is expected (spike S3); the disc is
  designed around it.

## Verifying the risky part

Spike S1 from the plan, in three steps. On a Windows machine with Steam:

```bash
dotnet run --project src/SteamDisc.Builder -- inspect 620
```

```bash
dotnet run --project src/SteamDisc.Builder -- package 620 --out D:\test
```

Uninstall the game in Steam, delete the folder, then:

```bash
dotnet run --project src/SteamDisc.Runtime -- D:\test\disc
```

Restart Steam and watch the download counter. Zero bytes means Path B works. Anything else means
the manifest was not faithful enough, and `inspect` plus the installer's warnings should say
which field was wrong.

The `--validate` flag implements the plan's Path C: mark the app as needing verification so Steam
re-checks the files locally instead of trusting our work. Slower, but self-healing.

## Layout

```
src/
  SteamDisc.Core        VDF, Steam locator, ACF model, archive, theming, images  — no UI
  SteamDisc.Install     the install engine: extract, prerequisites, manifest, hand-off
  SteamDisc.Authoring   the packaging engine: scan, advise, compress, span discs
  SteamDisc.Imaging     ISO 9660 + Joliet writer, autorun, burn hand-off
  SteamDisc.Art         Steam CDN, SteamGridDB and local art providers, cache
  SteamDisc.Covers      print geometry, template catalogue, PDF renderer
  SteamDisc.Skin        Avalonia skin views + theme→brush mapping, shared by both GUIs
  SteamDisc.Runtime     the console disc installer — the fallback and test harness
  SteamDisc.Runtime.App Setup.exe — the skinned Avalonia disc installer
  SteamDisc.Builder     the authoring CLI
  SteamDisc.Builder.App the authoring GUI
tests/
  SteamDisc.Tests       including a full package → install → image round trip
```

Core carries one dependency (`Microsoft.Win32.Registry`, to find Steam) and nothing else, so the
disc runtime stays small and auditable.

The engines are UI-free on purpose. The CLI drives exactly the same code a graphical Builder
would, so a GUI becomes a view over working code rather than a place where authoring logic
accumulates.

```bash
dotnet test
```

## Known constraints

- **Ownership.** Steam checks the account owns the app. Offline mode only works if the client has
  authenticated on that machine before.
- **Third-party DRM.** Ubisoft Connect, EA, Epic, anti-cheat — these phone home on first launch
  regardless. The Builder detects them and warns rather than pretending otherwise.
- **Games that patch constantly.** Live-service titles will download a delta immediately. Best
  suited: single-player games that have stopped updating. `list` flags this.
- **Optical read speed.** BD-R at 6× is roughly 27 MB/s. A 40 GB install is ~25 minutes at best,
  before decompression. Progress estimates are drawn from recent throughput, and no estimate is
  shown until one would mean something.
- **Disc rot.** Consumer BD-R is not archival. M-DISC exists if the goal is genuine long-term
  preservation rather than the retail-box experience.
- **SmartScreen.** An unsigned `Setup.exe` will be flagged. Code signing is milestone M8.

## Licence

Not yet chosen.
