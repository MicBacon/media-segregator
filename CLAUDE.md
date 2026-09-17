# Media Segregator — working instructions

Desktop app that **copies** phone photos and videos into a dated folder tree — the originals stay
where they are. Avalonia 12 on .NET 10, developed on macOS, **shipped as a self-contained Windows
executable**. One run may carry a hundred thousand small files or a single 100 GB video.

The UI is Polish. Code, identifiers, comments and commit messages are English.

For anything under `MediaSegregator.Tests/`, read [MediaSegregator.Tests/CLAUDE.md](MediaSegregator.Tests/CLAUDE.md) as well — it is the
authority on how tests are written here.

---

## Commands

```bash
dotnet build                                  # whole solution (MediaSegregator.slnx)
dotnet test                                   # 210 tests, ~300 ms — must be green before you finish
dotnet run --project MediaSegregator          # launch the app
```

The repository path contains a space (`Aurea Porta`). **Quote paths in every shell command.**

Publishing (the command in [README.md](README.md) is the contract — update it if targets change):

```bash
dotnet publish MediaSegregator/MediaSegregator.csproj \
  -c Release -r win-x64 --self-contained true \
  -p:PublishSingleFile=true \
  -p:IncludeNativeLibrariesForSelfExtract=true
```

---

## Architecture

A handful of pure-ish core types do the work; the window is a thin shell over them.

| File | Role |
| --- | --- |
| [FileScanner.cs](MediaSegregator/FileScanner.cs) | Enumerates the **top level** of a folder, keeps known media extensions, returns `ScanResult` |
| [Metadata.cs](MediaSegregator/Metadata.cs) | One `ImageMetadataReader` pass over a file, shared so routing reads it once rather than twice |
| [MediaDate.cs](MediaSegregator/MediaDate.cs) | Capture date from EXIF/QuickTime metadata, falling back to the date in the file name |
| [MediaLocation.cs](MediaSegregator/MediaLocation.cs) | GPS fix from the EXIF GPS IFD or the video's ISO 6709 string, as a `GeoPoint` |
| [Places.cs](MediaSegregator/Places.cs) | `GeoPoint` → nearest settlement within 15 km, from the embedded GeoNames list: worldwide `cities500` plus the full Poland list; coordinates otherwise |
| [DestinationLayout.cs](MediaSegregator/DestinationLayout.cs) | `2026_03_01/Zdjęcia/Warszawa` vs `Bez daty/Wideo` — pure string work |
| [FileCopier.cs](MediaSegregator/FileCopier.cs) | Does the I/O; routing is injected as `Func<ScannedFile, CopyTarget>?` |
| [FileMover.cs](MediaSegregator/FileMover.cs) | **Parked, unreferenced, untested.** Moving instead of copying, kept for a future option — read its doc comment before wiring it up |
| [MainWindow.axaml.cs](MediaSegregator/MainWindow.axaml.cs) | Window that is its own view model (`INotifyPropertyChanged`) |
| [AppSettings.cs](MediaSegregator/AppSettings.cs) | Last-used folders, JSON under `%AppData%/MediaSegregator` |

Flow: `Scan` → `ScanResult.Files` held in `_files` → user presses *Kopiuj* →
`FileCopier.Copy(_files, destination, DestinationLayout.TargetFor, token, progress)` on a background
thread → `CopyProgress` drives the bar while it runs, `CopyResult` is rendered into the status line.

**The seam that keeps this testable is the `targetFor` delegate.** `FileCopier` never calls
`MediaDate`, `MediaLocation`, `Places` or `DestinationLayout` itself, and
`DestinationLayout.SubfolderFor` never touches a disk. Keep it that way: new sorting rules go into a
pure function plus a delegate, not into the copier's loop and never into the code-behind.

---

## Invariants — do not regress these

Each one is covered by tests, and each exists for a reason worth reading before you change it.

1. **Nothing is ever overwritten, and the source is never touched.** A name clash gets a ` (1)`,
   ` (2)`… suffix; a *directory* holding the name counts as a clash too.
2. **The filesystem timestamp is never used as a capture date.** On media copied off a phone it is
   the date of the copy, so trusting it files a whole library under one wrong day. Undated media
   goes to `Bez daty`.
3. **Re-runs settle.** Copying leaves the originals in place, so nothing else stops a second run
   from duplicating the library: a candidate name whose file already exists **with the same
   length** is skipped, walking the ` (n)` suffixes as it goes. Same name and length is taken to
   mean same file — comparing content would mean re-reading everything on every run, which is the
   cost the rule exists to avoid. A file already sitting in its target folder is skipped too.
4. **Per-file failures are collected, not thrown.** One unreadable file must not abort a run of
   several thousand. `OperationCanceledException` is the exception: it escapes the collector.
5. **Dates outside 1990 … tomorrow are implausible** and rejected — a camera with a dead clock
   emits 1970 or 1980. Coordinates outside the globe, and exactly (0, 0), are rejected the same way.
6. **`Undated` counts only files that actually copied**, so a re-run never reports
   "copied 0 · undated 1".
7. **Settings never throw**, and neither does `Places`. A missing or corrupt `settings.json` means
   "no preferences yet"; a missing or corrupt `cities.tsv` means "every fix is named after its
   coordinates".
8. **Scanning is non-recursive.** Top level only, deliberately.
9. **Paths are built with `Path.Combine` and compared through `FileCopier.PathComparison`**
   (ordinal on macOS, ordinal-ignore-case on Windows). Never hardcode `/` or `\`.
10. **A half-written copy never wears its final name.** Bytes go to `<name>.part` and are renamed
    into place only once the whole file is down; a failure or a cancel deletes the partial. This is
    what keeps rule 3 honest when a 100 GB copy is interrupted. A *killed* process cannot clean up
    after itself, so a run also sweeps `*.part` out of each destination folder on its first visit
    to it — bounded by the number of folders a run touches, never a walk of the whole tree.
11. **A copy is cancellable and reports progress.** `File.Copy` is one blocking call that can do
    neither, which is why `FileCopier` streams through its own buffer. Reports are throttled — a
    100 GB file must not post a hundred thousand updates at the UI thread, and neither must a
    hundred thousand small files, so *every* report goes through the interval, skips included.
    What a run guarantees is its last one: the finished tally, forced after the loop, and forced
    again on the way out of a cancel so the window can still say how far it got.
12. **A tally counts bytes that are on a disk.** A file that fails or is cancelled mid-stream has
    its partial deleted, so `BytesCopied` is wound back past it — otherwise a run interrupted 60 GB
    into a 100 GB video would claim to have copied them.
13. **Progress measures the work, not the writing.** `BytesSettled` counts every file the loop got
    through — copied, skipped or failed — while `BytesDone` counts only what was written. The bar
    is driven by the first, or a second run over a sorted library would sit at nought from start to
    finish while doing exactly what it was asked to. A cancelled file settles nothing: its partial
    is gone, so it was never dealt with.
14. **Free space is a warning, never a refusal.** `FileCopier.RoomFor` weighs a run against the
    destination volume after a scan, but the scan's total is the whole library and a re-run copies
    almost none of it — blocking on that number would lock the user out of the cheap repeat run
    rule 3 exists to provide. A volume the system will not measure (a UNC share) counts as fitting.

---

## Code style

Match the surrounding code. The house style is unusually consistent — follow it literally.

- **Explicit types everywhere. `var` is not used in this codebase.** `MoveResult result = …`, not
  `var result = …`.
- File-scoped namespaces. Braces on every control-flow body, including single statements.
- Expression-bodied members when the body is one expression; otherwise a normal block with a blank
  line before the closing `return`.
- Modern C# is welcome and already used: collection expressions (`[]`, `[.. items]`), target-typed
  `new()`, `is { } value` patterns, `record` / `readonly record struct`, `[GeneratedRegex]`.
- `sealed record` for data, `static class` for behaviour. There is no DI container and no MVVM
  framework — **do not add one**.
- Nullable is enabled. Use `!` only where the surrounding code proves it safe, and say why in a
  comment (see `KindOf(entry.FileName)!.Value`).
- XML doc comments on public members. Comments explain **why**, never what the line already says.
  Long explanations of a real-world quirk (Android writing local time into `mvhd`) are the point,
  not clutter.
- No `#region`. No `.editorconfig` to satisfy — the code itself is the reference.

## Avalonia specifics

- Compiled bindings are on by default and `MainWindow.axaml` declares `x:DataType="local:MainWindow"`.
  Every binding must resolve against a real `MainWindow` member or the build fails.
- A new bindable value means a private field plus a property that calls `Set(ref _field, value)`.
  `MainWindow` shadows Avalonia's `PropertyChanged` with `public new event PropertyChangedEventHandler?` —
  bindings depend on that member, so leave it alone.
- **Every disk operation goes through `Task.Run`.** Scanning and copying must never run on the UI
  thread.
- `IsBusy` gates the buttons during a copy; keep new long-running actions behind it. *Anuluj* is the
  one button enabled *while* `IsBusy`.
- `IProgress<CopyProgress>` is built on the UI thread as a `Progress<T>`, so it marshals its own
  reports back; never touch a bindable property from the background thread.
- Status text is Polish, uses `N0`/`N1` numeric formats through the `Size` helper, and reports
  elapsed milliseconds.

---

## Common tasks

**Support a new media format** — add one entry to `MediaExtensions` in
[FileScanner.cs](MediaSegregator/FileScanner.cs#L32) and one `[InlineData]` case to the matching
theory in `FileScannerTests`. Note that MetadataExtractor ships no Matroska reader, so `.mkv` and
`.webm` can only ever get their date from the file name.

**Support a new camera's file-name date** — add a `[GeneratedRegex]` partial method in
[MediaDate.cs](MediaSegregator/MediaDate.cs) with named groups `y`, `m`, `d` and digit boundaries
(`(?<!\d)` / `(?!\d)` — they are what stops epoch milliseconds parsing as a date), then list it in
`Patterns` **most specific first**. Add the real-world file name to the `FromFileName` theory.

**Change the folder tree** — `DestinationLayout.SubfolderFor` only. It stays pure and disk-free.

**Add a new metadata source** — extend the `??` chain in `MediaDate.FromMetadata` or
`MediaLocation.Of`, ordered by how much you trust each tag, and build a byte-level fixture for it
in the tests. Both read from directories handed in by `Metadata.Read`, so a new source costs
nothing extra per file — `DestinationLayout.TargetFor` reads each file once and asks it twice.

**Change how places are named** — `Places.NameFor`. The 15 km threshold and the coordinate fallback
live there. The list covers the world through GeoNames `cities500`, with the full Polish list kept
for domestic village-level names; the data file is regenerated by the recipe in
[README.md](README.md#place-names), never edited by hand. Two Polish rows are the exception and are
corrected in place: GeoNames' Polish alternate name for Motarzyn was a Wikipedia URL and the one for
a Nowa Wieś was a holiday-let advertisement, both of which had displaced the villages' own names.
The recipe now rejects such alternates, so a regeneration reproduces the corrected file rather than
undoing it — see `NameFor_RowsWhoseAlternateNameWasNotAName`.

**Touch the copy loop** — re-read invariants 1, 3, 10, 11 and 13 first, then check that a run over an
already-sorted tree still copies nothing *and* still fills its progress bar.

---

## Before you call it done

- `dotnet test` is green.
- New behaviour has tests, per [MediaSegregator.Tests/CLAUDE.md](MediaSegregator.Tests/CLAUDE.md).
- No `var`, no new dependency, no logic moved into the code-behind.
- If you changed how files are copied, re-read the invariants above and check the run is still
  idempotent.

`.claude/`, `.vscode/`, `bin/` and `obj/` are gitignored. Commit only when asked.
