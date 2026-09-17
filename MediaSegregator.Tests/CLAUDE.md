# Writing tests for Media Segregator

The suite is 210 tests that run in about 300 ms with no mocks, no fakes and no committed fixture
binaries. It is worth keeping it that way. Read one existing file end to end before adding to it —
[FileCopierTests.cs](FileCopierTests.cs) for I/O, [MediaLocationTests.cs](MediaLocationTests.cs) and
[MediaDateTests.cs](MediaDateTests.cs) for binary fixtures,
[DestinationLayoutTests.cs](DestinationLayoutTests.cs) for pure logic.

```bash
dotnet test                                                    # everything
dotnet test --filter "FullyQualifiedName~FileCopier"           # one class
dotnet test --filter "FullyQualifiedName~Copy_RepeatedClashes" # one test
dotnet test --collect:"XPlat Code Coverage"                    # coverlet is already referenced
```

Stack: xUnit 2.9.3 with `using Xunit` declared globally in the csproj — **never add a `using Xunit;`
line to a test file**. There is no Moq, no FluentAssertions, no AutoFixture, and none should be
added; real files in a temp folder are faster and clearer than a mocked filesystem.

---

## Shape of a test class

```csharp
namespace MediaSegregator.Tests;

/// <summary>
/// What this class covers, and — just as usefully — what it deliberately does not.
/// </summary>
public sealed class ThingTests : IDisposable
{
    private readonly string _root;

    public ThingTests()   // xUnit builds a fresh instance per test: this is per-test setup
    {
        _root = Path.Combine(Path.GetTempPath(), "mediasegregator-tests", Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(_root);
    }

    public void Dispose()
    {
        try
        {
            if (Directory.Exists(_root))
            {
                Directory.Delete(_root, recursive: true);
            }
        }
        catch (IOException)
        {
            // A leftover temp folder must never fail an otherwise green test.
        }
    }
}
```

- Test classes are `sealed`. One class per production type.
- The GUID-named root is what makes the suite parallel-safe — xUnit runs classes concurrently, so
  **no test may touch a fixed path, the working directory, or anything under the user's profile**.
- Teardown swallows `IOException` only. Never let cleanup turn a passing test red.
- Classes that need no disk (`DestinationLayoutTests`) implement no `IDisposable` and create nothing.

## Naming

`Method_Scenario_ExpectedOutcome`, written as English a reviewer can read out loud:

```
Copy_SameNameDifferentLength_GetsANumberedSuffix
Copy_CancelledMidRun_KeepsWhatItAlreadyCopied
Copy_FolderSharingAPrefixWithDestination_IsNotTreatedAsTheDestination
Taken_NeverUsesTheFileSystemTimestamp
SubfolderFor_KeepsAsciiDigits_UnderALocaleThatDoesNot
```

The scenario half may be dropped when the method name already says everything
(`Scan_ClassifiesPhotos`). Never number tests, never write `Test1`, never name a test after the
implementation detail it happens to exercise.

## Body

Arrange, act, assert — separated by blank lines, with no `// Arrange` comments. Three to eight
lines is the norm.

```csharp
[Fact]
public void Copy_SingleFile_LeavesTheOriginalAndCreatesTheCopy()
{
    string path = WriteFile(_source, "holiday.jpg", "bytes");

    CopyResult result = FileCopier.Copy([Scan(path)], _destination);

    Assert.Equal(1, result.Copied);
    Assert.Equal("bytes", File.ReadAllText(path));
    Assert.Equal("bytes", File.ReadAllText(Path.Combine(_destination, "holiday.jpg")));
}
```

Assert both halves of a copy: the original is still there **and** the copy exists. A test that only
checked the destination would pass on a move.

Content lengths matter now. Two files of the same name and the same length are taken to be the same
file, so a collision test whose fixtures both hold eight bytes proves nothing — give them different
lengths, as `Copy_RepeatedClashes_KeepCountingUp` does.

## Style rules that also apply here

The production style from [../CLAUDE.md](../CLAUDE.md) holds inside the test project too:
explicit types (**no `var`**), file-scoped namespaces, braces on every body, collection expressions.
Plus:

- Group related tests with a banner comment; keep the sections in a sensible order.

  ```csharp
  // ---------------------------------------------------------- name collisions
  ```

- Private helpers get an XML doc line and live under a `// ------ helpers` banner, at the bottom of
  the class (or just after the fixture section when several sections use them).
- A comment on a test earns its place when it explains **why the case matters in the real world** —
  `"…/destination/../destination/loop.jpg is the very same file, just spelled awkwardly."` Do not
  narrate what the assertions already state.

## Theories

Use `[Theory]` when the same assertion runs over a list of inputs, and annotate each case with the
device or app it came from — that provenance is the most valuable thing in these tables:

```csharp
[Theory]
[InlineData("IMG_20260301_142233.jpg")]              // Android stock camera
[InlineData("PXL_20260301_142233123.jpg")]           // Pixel, milliseconds appended
[InlineData("IMG-20260301-WA0001.jpg")]              // WhatsApp
[InlineData("Zrzut ekranu 2026-03-1 o 17.09.31.png")] // macOS pl, unpadded day
public void FromFileName_ReadsTheDateCameraAppsBakeIn(string name)
{
    Assert.Equal(new DateTime(2026, 3, 1), MediaDate.FromFileName(name));
}
```

Keep every theory case asserting the same thing. If one input needs a different expectation, give it
its own `[Fact]`.

## Assertions

- Argument order is `Assert.Equal(expected, actual)`. Getting it backwards makes failure messages lie.
- `Assert.Single` returns the item — use it: `string error = Assert.Single(result.Errors);`
- Compare collections against an array literal, sorted with an explicit `StringComparer.Ordinal`
  when order would otherwise be filesystem-dependent.
- `Assert.Throws<T>` when the exact type is the contract; `Assert.ThrowsAny<T>` when the framework
  may throw a subclass (`Assert.ThrowsAny<IOException>`).
- Assert on observable behaviour through the public API. **Never widen a member's visibility, add an
  interface, or reach in with reflection just to make something testable** — if a behaviour is hard
  to reach, that is a design signal, and the fix is a pure function with an injected delegate (see
  `FileCopier`'s `targetFor`).

## Fixtures

**Build binary fixtures byte by byte in code.** No `.jpg` or `.mp4` is committed to this repo. The
hand-built JPEG and MP4 in [MediaDateTests.cs](MediaDateTests.cs) and
[MediaLocationTests.cs](MediaLocationTests.cs) are the pattern: a documented minimal container, with
the byte writers (`U16`, `B32`, …) as small private statics and a comment naming each field. The
reader can then see exactly which metadata is under test, which no binary blob can offer.

`Data/cities.tsv` is the exception to "no committed fixtures": it is not a fixture but the data the
app ships, so [PlacesTests.cs](PlacesTests.cs) asserts against real towns and their real
coordinates rather than a stand-in that could drift from what users get.

**Write text files for everything else.** `WriteFile(folder, name, content)` with distinct contents
per file so a test can prove *which* file landed where:

```csharp
Assert.Equal("incoming", File.ReadAllText(Path.Combine(_destination, "clash (1).jpg")));
```

## Portability and determinism

- Build expected paths with `Path.Combine`, never a literal `"2026_03_01/Zdjęcia"` — the suite must
  pass on Windows, which is the deployment target.
- Set `CultureInfo.CurrentCulture` explicitly when a test is about culture, and restore it in a
  `finally`.
- No `Thread.Sleep`, no timing assertions, no network, no dependence on today's date beyond the
  plausibility window the production code defines.
- Nothing writes outside the per-test temp root. `AppSettings` resolves to the real
  `%AppData%` / `~/Library/Application Support`, which is why it has no tests — **do not write one
  that round-trips through `AppSettings.FilePath`**. If settings need coverage, refactor the path in
  as a parameter first.

---

## What to cover for a new behaviour

Work through this list; each row corresponds to bugs the existing suite already catches.

| Angle | Example from the suite |
| --- | --- |
| Happy path | `Copy_SingleFile_LeavesTheOriginalAndCreatesTheCopy` |
| Empty input | `Copy_EmptySequence_ReturnsZeroedResult` |
| Volume | `Copy_ManyFiles_CopiesEveryOne` (50 files) |
| Size | `Copy_FileLargerThanTheBuffer_CopiesEveryByte` (3 MB, several turns of the buffer) |
| Collisions | `Copy_RepeatedClashes_KeepCountingUp`, `Copy_ClashWithADirectory_AlsoGetsASuffix` |
| Odd names | Unicode, emoji, spaces, `.hidden`, `clip.2026-09-09.mp4`, extensionless |
| Odd paths | trailing separator, `..` segments, prefix-sharing sibling folders |
| Idempotence | `Copy_RunTwice_CopiesNothingTheSecondTime`, `Copy_IntoTheDatedTreeTwice_IsANoOpTheSecondTime` |
| Errors | missing file, source is a directory, blank path — one error each, run continues |
| Cancellation | pre-cancelled token, cancelled mid-run, and that it escapes the error collector |
| Leftovers | `Copy_LeavesNoPartialFileBehind`, no `*.part` after a cancel or an error, and `Copy_OrphanedPartialInTheDestination_IsSwept` for the ones a killed process left |
| Progress | `Copy_ReportsTheFinishedTally_WhenTheRunEnds`, `Copy_SettlesEveryFile_WhetherItCopiedSkippedOrFailed`, and nothing at all for an empty sequence |
| Boundaries | leap day, year rollover, month 13, 30 February, 1899, 2099, (0, 0), ±90, ±180 |
| Laziness | `Copy_EnumeratesTheSequenceLazilyAndExactlyOnce` |

Also test the behaviour you deliberately chose to leave rough, so it is documented rather than
accidental — `Copy_NullSequence_ThrowsAfterCreatingTheDestination` records that there is no argument
guard and that the folder is created anyway, and
`Copy_SameNameAndLengthButDifferentContent_IsSkipped` records the cost of matching on length.

## Not covered, on purpose

`DestinationLayout.TargetFor` **is** tested now, in the wiring section at the bottom of
[DestinationLayoutTests.cs](DestinationLayoutTests.cs), against real but deliberately
metadata-free files — it is where the readers and the place list meet, and it was the one seam
nothing exercised. The readers themselves stay covered byte by byte in their own classes.

`MainWindow` has no tests: it is code-behind over Avalonia, and driving it would need a headless
harness for very little return. The rule that keeps this acceptable is that **no logic lives there** —
if you find yourself wanting to test the window, move the logic into a pure type and test that
instead.

`FileMover` has no tests either, and this one is a debt rather than a design choice. Its suite
became `FileCopierTests` when the app switched to copying, and the type itself was kept — parked and
unreferenced — for a future "move instead of copy" option. Nothing exercises it, so **assume it has
drifted**: anyone wiring it up owes it a test class first, modelled on `FileCopierTests` minus the
same-name-and-length rule, which moving does not need.

## Checklist

- [ ] `dotnet test` green, and the new test fails when the production change is reverted.
- [ ] Name reads as a sentence; assertions are `(expected, actual)`.
- [ ] No `var`, no `using Xunit;`, no new package.
- [ ] Nothing written outside the GUID temp root; no fixed paths.
- [ ] Expected paths built with `Path.Combine`.
- [ ] Placed in the right section, under the right banner, with any real-world provenance noted.
