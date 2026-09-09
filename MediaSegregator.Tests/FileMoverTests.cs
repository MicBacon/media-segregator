namespace MediaSegregator.Tests;

/// <summary>
/// Every test gets a throwaway tree under the OS temp folder:
///
///     &lt;root&gt;/source        files start here
///     &lt;root&gt;/destination   files are moved here
///
/// xUnit builds a fresh instance per test, so the constructor/Dispose pair below is a
/// per-test setup and teardown — no state leaks between cases.
/// </summary>
public sealed class FileMoverTests : IDisposable
{
    private readonly string _root;
    private readonly string _source;
    private readonly string _destination;

    public FileMoverTests()
    {
        _root = Path.Combine(Path.GetTempPath(), "mediasegregator-tests", Guid.NewGuid().ToString("N"));
        _source = Path.Combine(_root, "source");
        _destination = Path.Combine(_root, "destination");

        Directory.CreateDirectory(_source);
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

    // ---------------------------------------------------------------- helpers

    /// <summary>Writes <paramref name="content"/> to <paramref name="folder"/>/<paramref name="name"/>.</summary>
    private static string WriteFile(string folder, string name, string content = "payload")
    {
        Directory.CreateDirectory(folder);
        string path = Path.Combine(folder, name);
        File.WriteAllText(path, content);
        return path;
    }

    /// <summary>A <see cref="ScannedFile"/> describing a real file on disk.</summary>
    private static ScannedFile Scan(string path, string? name = null) =>
        new(path,
            name ?? Path.GetFileName(path),
            File.Exists(path) ? new FileInfo(path).Length : 0,
            File.Exists(path) ? File.GetLastWriteTime(path) : DateTime.MinValue,
            MediaKind.Photo);

    /// <summary>File names directly under the destination folder, sorted for stable asserts.</summary>
    private string[] DestinationNames() =>
        Directory.Exists(_destination)
            ? [.. Directory.GetFiles(_destination).Select(Path.GetFileName).OrderBy(n => n, StringComparer.Ordinal)!]
            : [];

    // ------------------------------------------------------- destination setup

    [Fact]
    public void Move_CreatesDestinationFolder_WhenItDoesNotExist()
    {
        Assert.False(Directory.Exists(_destination));

        FileMover.Move([], _destination);

        Assert.True(Directory.Exists(_destination));
    }

    [Fact]
    public void Move_CreatesNestedDestinationFolders()
    {
        string nested = Path.Combine(_destination, "2026", "09", "photos");

        FileMover.Move([], nested);

        Assert.True(Directory.Exists(nested));
    }

    // ------------------------------------------------------------- happy path

    [Fact]
    public void Move_EmptySequence_ReturnsZeroedResult()
    {
        MoveResult result = FileMover.Move([], _destination);

        Assert.Equal(0, result.Moved);
        Assert.Equal(0, result.Skipped);
        Assert.Empty(result.Errors);
    }

    [Fact]
    public void Move_SingleFile_RemovesSourceAndCreatesTarget()
    {
        string path = WriteFile(_source, "holiday.jpg", "bytes");

        MoveResult result = FileMover.Move([Scan(path)], _destination);

        Assert.Equal(1, result.Moved);
        Assert.Equal(0, result.Skipped);
        Assert.Empty(result.Errors);
        Assert.False(File.Exists(path));
        Assert.True(File.Exists(Path.Combine(_destination, "holiday.jpg")));
    }

    [Fact]
    public void Move_PreservesFileContentByteForByte()
    {
        byte[] payload = [0xFF, 0xD8, 0xFF, 0xE0, 0x00, 0x10, 0x4A, 0x46];
        string path = Path.Combine(_source, "raw.jpg");
        File.WriteAllBytes(path, payload);

        FileMover.Move([Scan(path)], _destination);

        Assert.Equal(payload, File.ReadAllBytes(Path.Combine(_destination, "raw.jpg")));
    }

    [Fact]
    public void Move_ManyFiles_MovesEveryOne()
    {
        ScannedFile[] files = [.. Enumerable.Range(0, 50)
            .Select(i => Scan(WriteFile(_source, $"img-{i:D3}.jpg", i.ToString())))];

        MoveResult result = FileMover.Move(files, _destination);

        Assert.Equal(50, result.Moved);
        Assert.Empty(result.Errors);
        Assert.Empty(Directory.GetFiles(_source));
        Assert.Equal(50, Directory.GetFiles(_destination).Length);
    }

    [Fact]
    public void Move_GathersFilesFromSeveralSourceFolders()
    {
        string a = WriteFile(Path.Combine(_source, "camera"), "a.jpg");
        string b = WriteFile(Path.Combine(_source, "downloads"), "b.mp4");

        MoveResult result = FileMover.Move([Scan(a), Scan(b)], _destination);

        Assert.Equal(2, result.Moved);
        Assert.Equal(["a.jpg", "b.mp4"], DestinationNames());
    }

    [Fact]
    public void Move_EmptyFile_IsMovedLikeAnyOther()
    {
        string path = WriteFile(_source, "empty.mp4", string.Empty);

        MoveResult result = FileMover.Move([Scan(path)], _destination);

        Assert.Equal(1, result.Moved);
        Assert.Equal(0, new FileInfo(Path.Combine(_destination, "empty.mp4")).Length);
    }

    [Fact]
    public void Move_IgnoresLengthModifiedAndKindMetadata()
    {
        string path = WriteFile(_source, "mismatched.jpg", "real content");

        // Deliberately bogus metadata: the mover only ever looks at Path and Name.
        ScannedFile stale = new(path, "mismatched.jpg", Length: 999_999, Modified: DateTime.MinValue, MediaKind.Video);

        MoveResult result = FileMover.Move([stale], _destination);

        Assert.Equal(1, result.Moved);
        Assert.Equal("real content", File.ReadAllText(Path.Combine(_destination, "mismatched.jpg")));
    }

    [Fact]
    public void Move_UnicodeAndSpacedNames_SurviveIntact()
    {
        string path = WriteFile(_source, "wakacje – Kraków 2026 🌞.jpg");

        MoveResult result = FileMover.Move([Scan(path)], _destination);

        Assert.Equal(1, result.Moved);
        Assert.True(File.Exists(Path.Combine(_destination, "wakacje – Kraków 2026 🌞.jpg")));
    }

    // ------------------------------------------------ already in destination

    [Fact]
    public void Move_FileAlreadyInDestination_IsSkippedNotRenamed()
    {
        string path = WriteFile(_destination, "in-place.jpg", "same");

        MoveResult result = FileMover.Move([Scan(path)], _destination);

        Assert.Equal(0, result.Moved);
        Assert.Equal(1, result.Skipped);
        Assert.Empty(result.Errors);
        Assert.Equal(["in-place.jpg"], DestinationNames());
        Assert.Equal("same", File.ReadAllText(path));
    }

    [Fact]
    public void Move_SkipsInPlaceFilesButStillMovesTheRest()
    {
        ScannedFile inPlace = Scan(WriteFile(_destination, "already.jpg"));
        ScannedFile incoming = Scan(WriteFile(_source, "incoming.jpg"));

        MoveResult result = FileMover.Move([inPlace, incoming], _destination);

        Assert.Equal(1, result.Moved);
        Assert.Equal(1, result.Skipped);
        Assert.Equal(["already.jpg", "incoming.jpg"], DestinationNames());
    }

    [Fact]
    public void Move_NormalisesRelativeSourcePaths_BeforeTheInPlaceCheck()
    {
        WriteFile(_destination, "loop.jpg");

        // …/destination/../destination/loop.jpg is the very same file, just spelled awkwardly.
        string roundabout = Path.Combine(_destination, "..", "destination", "loop.jpg");

        MoveResult result = FileMover.Move([new ScannedFile(roundabout, "loop.jpg", 0, DateTime.Now, MediaKind.Photo)], _destination);

        Assert.Equal(1, result.Skipped);
        Assert.Equal(0, result.Moved);
        Assert.Equal(["loop.jpg"], DestinationNames());
    }

    [Fact]
    public void Move_SubfolderOfDestination_IsMovedNotSkipped()
    {
        // A file one level down is *not* "already in the destination".
        string nested = WriteFile(Path.Combine(_destination, "inbox"), "nested.jpg");

        MoveResult result = FileMover.Move([Scan(nested)], _destination);

        Assert.Equal(1, result.Moved);
        Assert.Equal(0, result.Skipped);
        Assert.False(File.Exists(nested));
        Assert.Equal(["nested.jpg"], DestinationNames());
    }

    [Fact]
    public void Move_FolderSharingAPrefixWithDestination_IsNotTreatedAsTheDestination()
    {
        // "destination-old" starts with "destination" but is a different folder entirely.
        string path = WriteFile(_root + Path.DirectorySeparatorChar + "destination-old", "prefix.jpg");

        MoveResult result = FileMover.Move([Scan(path)], _destination);

        Assert.Equal(1, result.Moved);
        Assert.Equal(0, result.Skipped);
        Assert.False(File.Exists(path));
    }

    // ------------------------------------------------------- name collisions

    [Fact]
    public void Move_NameClash_AppendsNumberedSuffix()
    {
        WriteFile(_destination, "clash.jpg", "existing");
        string incoming = WriteFile(_source, "clash.jpg", "incoming");

        MoveResult result = FileMover.Move([Scan(incoming)], _destination);

        Assert.Equal(1, result.Moved);
        Assert.Equal(["clash.jpg", "clash (1).jpg"], DestinationNames().OrderBy(n => n.Length).ToArray());
        Assert.Equal("existing", File.ReadAllText(Path.Combine(_destination, "clash.jpg")));
        Assert.Equal("incoming", File.ReadAllText(Path.Combine(_destination, "clash (1).jpg")));
    }

    [Fact]
    public void Move_RepeatedClashes_KeepCountingUp()
    {
        WriteFile(_destination, "dup.jpg", "0");
        ScannedFile[] files =
        [
            Scan(WriteFile(Path.Combine(_source, "a"), "dup.jpg", "1")),
            Scan(WriteFile(Path.Combine(_source, "b"), "dup.jpg", "2")),
            Scan(WriteFile(Path.Combine(_source, "c"), "dup.jpg", "3")),
        ];

        MoveResult result = FileMover.Move(files, _destination);

        Assert.Equal(3, result.Moved);
        Assert.Empty(result.Errors);
        Assert.Equal("1", File.ReadAllText(Path.Combine(_destination, "dup (1).jpg")));
        Assert.Equal("2", File.ReadAllText(Path.Combine(_destination, "dup (2).jpg")));
        Assert.Equal("3", File.ReadAllText(Path.Combine(_destination, "dup (3).jpg")));
    }

    [Fact]
    public void Move_SkipsSuffixesThatAreAlreadyTaken()
    {
        WriteFile(_destination, "gap.jpg", "0");
        WriteFile(_destination, "gap (1).jpg", "1");
        WriteFile(_destination, "gap (2).jpg", "2");
        string incoming = WriteFile(_source, "gap.jpg", "3");

        FileMover.Move([Scan(incoming)], _destination);

        Assert.Equal("3", File.ReadAllText(Path.Combine(_destination, "gap (3).jpg")));
    }

    [Fact]
    public void Move_ClashWithADirectory_AlsoGetsASuffix()
    {
        // Nothing may be dropped on top of a folder that happens to share the name.
        Directory.CreateDirectory(Path.Combine(_destination, "album.jpg"));
        string incoming = WriteFile(_source, "album.jpg", "photo");

        MoveResult result = FileMover.Move([Scan(incoming)], _destination);

        Assert.Equal(1, result.Moved);
        Assert.True(Directory.Exists(Path.Combine(_destination, "album.jpg")));
        Assert.Equal("photo", File.ReadAllText(Path.Combine(_destination, "album (1).jpg")));
    }

    [Fact]
    public void Move_DirectoryOccupyingTheSuffixedName_IsAlsoStepppedOver()
    {
        // The first candidate is taken by a file and the " (1)" candidate by a folder,
        // so the only free slot is " (2)".
        WriteFile(_destination, "trip.jpg", "existing");
        Directory.CreateDirectory(Path.Combine(_destination, "trip (1).jpg"));
        string incoming = WriteFile(_source, "trip.jpg", "incoming");

        MoveResult result = FileMover.Move([Scan(incoming)], _destination);

        Assert.Equal(1, result.Moved);
        Assert.True(Directory.Exists(Path.Combine(_destination, "trip (1).jpg")));
        Assert.Equal("incoming", File.ReadAllText(Path.Combine(_destination, "trip (2).jpg")));
    }

    [Fact]
    public void Move_ClashOnAnExtensionlessName_SuffixesTheBareName()
    {
        WriteFile(_destination, "NOTES");
        string incoming = WriteFile(_source, "NOTES", "second");

        FileMover.Move([Scan(incoming)], _destination);

        Assert.Equal("second", File.ReadAllText(Path.Combine(_destination, "NOTES (1)")));
    }

    [Fact]
    public void Move_ClashOnAMultiDotName_KeepsOnlyTheLastSegmentAsExtension()
    {
        WriteFile(_destination, "clip.2026-09-09.mp4");
        string incoming = WriteFile(_source, "clip.2026-09-09.mp4", "second");

        FileMover.Move([Scan(incoming)], _destination);

        Assert.Equal("second", File.ReadAllText(Path.Combine(_destination, "clip.2026-09-09 (1).mp4")));
    }

    [Fact]
    public void Move_ClashOnADotPrefixedName_TreatsTheWholeNameAsExtension()
    {
        // Path.GetFileNameWithoutExtension(".hidden") is empty, so the suffix leads the name.
        WriteFile(_destination, ".hidden");
        string incoming = WriteFile(_source, ".hidden", "second");

        FileMover.Move([Scan(incoming)], _destination);

        Assert.Equal("second", File.ReadAllText(Path.Combine(_destination, " (1).hidden")));
    }

    [Fact]
    public void Move_TwoDistinctFilesWithTheSameName_BothSurvive()
    {
        ScannedFile first = Scan(WriteFile(Path.Combine(_source, "phone"), "IMG_0001.jpg", "phone"));
        ScannedFile second = Scan(WriteFile(Path.Combine(_source, "camera"), "IMG_0001.jpg", "camera"));

        MoveResult result = FileMover.Move([first, second], _destination);

        Assert.Equal(2, result.Moved);
        Assert.Equal("phone", File.ReadAllText(Path.Combine(_destination, "IMG_0001.jpg")));
        Assert.Equal("camera", File.ReadAllText(Path.Combine(_destination, "IMG_0001 (1).jpg")));
    }

    [Fact]
    public void Move_UsesTheScannedName_NotTheNameOnDisk()
    {
        string path = WriteFile(_source, "on-disk.jpg", "content");

        MoveResult result = FileMover.Move([Scan(path, name: "renamed.jpg")], _destination);

        Assert.Equal(1, result.Moved);
        Assert.Equal(["renamed.jpg"], DestinationNames());
    }

    // --------------------------------------------------------- error handling

    [Fact]
    public void Move_MissingSourceFile_IsReportedAsAnError()
    {
        ScannedFile ghost = new(Path.Combine(_source, "ghost.jpg"), "ghost.jpg", 10, DateTime.Now, MediaKind.Photo);

        MoveResult result = FileMover.Move([ghost], _destination);

        Assert.Equal(0, result.Moved);
        Assert.Equal(0, result.Skipped);
        string error = Assert.Single(result.Errors);
        Assert.StartsWith("ghost.jpg: ", error);
        Assert.Empty(DestinationNames());
    }

    [Fact]
    public void Move_SourceIsADirectory_IsReportedAsAnError()
    {
        string folder = Path.Combine(_source, "not-a-file.jpg");
        Directory.CreateDirectory(folder);

        MoveResult result = FileMover.Move([new ScannedFile(folder, "not-a-file.jpg", 0, DateTime.Now, MediaKind.Photo)], _destination);

        Assert.Equal(0, result.Moved);
        Assert.Single(result.Errors);
        Assert.True(Directory.Exists(folder));
    }

    [Fact]
    public void Move_BlankSourcePath_IsReportedAsAnError()
    {
        // Path.GetFullPath("") throws — the per-file try/catch has to absorb it.
        MoveResult result = FileMover.Move([new ScannedFile("", "blank.jpg", 0, DateTime.Now, MediaKind.Photo)], _destination);

        Assert.Equal(0, result.Moved);
        Assert.StartsWith("blank.jpg: ", Assert.Single(result.Errors));
    }

    [Fact]
    public void Move_NamePointingIntoAMissingSubfolder_IsReportedAsAnError()
    {
        string path = WriteFile(_source, "sub.jpg");
        ScannedFile file = Scan(path, name: Path.Combine("nope", "sub.jpg"));

        MoveResult result = FileMover.Move([file], _destination);

        Assert.Equal(0, result.Moved);
        Assert.Single(result.Errors);
        Assert.True(File.Exists(path));
    }

    [Fact]
    public void Move_SameFileListedTwice_MovesOnceAndErrorsOnce()
    {
        ScannedFile file = Scan(WriteFile(_source, "twice.jpg"));

        MoveResult result = FileMover.Move([file, file], _destination);

        Assert.Equal(1, result.Moved);
        Assert.Single(result.Errors);
        Assert.Equal(["twice.jpg"], DestinationNames());
    }

    [Fact]
    public void Move_KeepsGoingAfterAFailure()
    {
        ScannedFile good1 = Scan(WriteFile(_source, "good1.jpg"));
        ScannedFile bad = new(Path.Combine(_source, "missing.jpg"), "missing.jpg", 0, DateTime.Now, MediaKind.Photo);
        ScannedFile good2 = Scan(WriteFile(_source, "good2.jpg"));

        MoveResult result = FileMover.Move([good1, bad, good2], _destination);

        Assert.Equal(2, result.Moved);
        Assert.Single(result.Errors);
        Assert.Equal(["good1.jpg", "good2.jpg"], DestinationNames());
    }

    [Fact]
    public void Move_ReportsOneErrorPerFailingFile()
    {
        ScannedFile[] files = [.. Enumerable.Range(0, 3)
            .Select(i => new ScannedFile(Path.Combine(_source, $"missing{i}.jpg"), $"missing{i}.jpg", 0, DateTime.Now, MediaKind.Photo))];

        MoveResult result = FileMover.Move(files, _destination);

        Assert.Equal(3, result.Errors.Count);
        Assert.All(result.Errors, e => Assert.Contains(": ", e));
    }

    [Fact]
    public void Move_DestinationIsAnExistingFile_ThrowsBeforeTouchingAnything()
    {
        string blocker = WriteFile(_root, "destination", "I am a file");
        ScannedFile file = Scan(WriteFile(_source, "any.jpg"));

        Assert.ThrowsAny<IOException>(() => FileMover.Move([file], blocker));

        Assert.True(File.Exists(Path.Combine(_source, "any.jpg")));
    }

    [Fact]
    public void Move_BlankDestination_Throws()
    {
        Assert.ThrowsAny<ArgumentException>(() => FileMover.Move([], ""));
    }

    [Fact]
    public void Move_NullDestination_Throws()
    {
        Assert.ThrowsAny<ArgumentNullException>(() => FileMover.Move([], null!));
    }

    // ------------------------------------------------------------ cancellation

    [Fact]
    public void Move_AlreadyCancelledToken_ThrowsBeforeMovingAnything()
    {
        ScannedFile file = Scan(WriteFile(_source, "untouched.jpg"));
        using CancellationTokenSource cts = new();
        cts.Cancel();

        Assert.Throws<OperationCanceledException>(() => FileMover.Move([file], _destination, cts.Token));

        Assert.True(File.Exists(Path.Combine(_source, "untouched.jpg")));
        Assert.Empty(DestinationNames());
    }

    [Fact]
    public void Move_CancelledMidRun_KeepsWhatItAlreadyMoved()
    {
        ScannedFile first = Scan(WriteFile(_source, "first.jpg"));
        ScannedFile second = Scan(WriteFile(_source, "second.jpg"));
        using CancellationTokenSource cts = new();

        IEnumerable<ScannedFile> Cancelling()
        {
            yield return first;
            cts.Cancel();
            yield return second;
        }

        Assert.Throws<OperationCanceledException>(() => FileMover.Move(Cancelling(), _destination, cts.Token));

        Assert.Equal(["first.jpg"], DestinationNames());
        Assert.True(File.Exists(Path.Combine(_source, "second.jpg")));
    }

    [Fact]
    public void Move_CancellationEscapesTheErrorCollector()
    {
        // The catch-all must not swallow the cancellation into the Errors list.
        ScannedFile file = Scan(WriteFile(_source, "one.jpg"));
        using CancellationTokenSource cts = new();
        cts.Cancel();

        OperationCanceledException ex =
            Assert.Throws<OperationCanceledException>(() => FileMover.Move([file], _destination, cts.Token));

        Assert.Equal(cts.Token, ex.CancellationToken);
    }

    [Fact]
    public void Move_DefaultToken_NeverCancels()
    {
        ScannedFile file = Scan(WriteFile(_source, "plain.jpg"));

        MoveResult result = FileMover.Move([file], _destination);

        Assert.Equal(1, result.Moved);
    }

    // -------------------------------------------------------------- lazy input

    [Fact]
    public void Move_EnumeratesTheSequenceLazilyAndExactlyOnce()
    {
        int enumerations = 0;
        ScannedFile file = Scan(WriteFile(_source, "lazy.jpg"));

        IEnumerable<ScannedFile> Counting()
        {
            enumerations++;
            yield return file;
        }

        MoveResult result = FileMover.Move(Counting(), _destination);

        Assert.Equal(1, enumerations);
        Assert.Equal(1, result.Moved);
    }

    [Fact]
    public void Move_NullSequence_ThrowsAfterCreatingTheDestination()
    {
        // No argument guard: the foreach dereferences null, and only after the
        // destination folder has already been created as a side effect.
        Assert.Throws<NullReferenceException>(() => FileMover.Move(null!, _destination));
        Assert.True(Directory.Exists(_destination));
    }

    // ------------------------------------------------ destination path shapes

    /// <summary>
    /// A folder picker or a settings round-trip can easily hand over "…/destination/".
    /// GetFullPath keeps that trailing separator while GetDirectoryName never emits one,
    /// so the in-place check has to normalise before comparing.
    /// </summary>
    [Fact]
    public void Move_DestinationWithTrailingSeparator_StillSkipsInPlaceFiles()
    {
        string path = WriteFile(_destination, "in-place.jpg", "same");

        MoveResult result = FileMover.Move([Scan(path)], _destination + Path.DirectorySeparatorChar);

        Assert.Equal(1, result.Skipped);
        Assert.Equal(0, result.Moved);
        Assert.Equal(["in-place.jpg"], DestinationNames());
        Assert.Equal("same", File.ReadAllText(path));
    }

    [Fact]
    public void Move_DestinationWithTrailingSeparator_StillMovesIncomingFiles()
    {
        ScannedFile file = Scan(WriteFile(_source, "incoming.jpg"));

        MoveResult result = FileMover.Move([file], _destination + Path.DirectorySeparatorChar);

        Assert.Equal(1, result.Moved);
        Assert.Equal(["incoming.jpg"], DestinationNames());
    }

    [Fact]
    public void Move_DestinationWithRedundantSegments_StillSkipsInPlaceFiles()
    {
        string path = WriteFile(_destination, "in-place.jpg", "same");
        string roundabout = Path.Combine(_destination, "..", "destination");

        MoveResult result = FileMover.Move([Scan(path)], roundabout);

        Assert.Equal(1, result.Skipped);
        Assert.Equal(0, result.Moved);
        Assert.Equal(["in-place.jpg"], DestinationNames());
    }
}
