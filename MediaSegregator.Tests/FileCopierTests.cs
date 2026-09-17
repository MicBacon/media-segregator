namespace MediaSegregator.Tests;

/// <summary>
/// Every test gets a throwaway tree under the OS temp folder:
///
///     &lt;root&gt;/source        files start here, and stay here
///     &lt;root&gt;/destination   copies land here
///
/// xUnit builds a fresh instance per test, so the constructor/Dispose pair below is a
/// per-test setup and teardown — no state leaks between cases.
///
/// Assert both halves of a copy: the original is still there **and** the copy exists. A test that
/// only checked the destination would pass on a move.
/// </summary>
public sealed class FileCopierTests : IDisposable
{
    private readonly string _root;
    private readonly string _source;
    private readonly string _destination;

    public FileCopierTests()
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

    /// <summary>Half-written copies anywhere under the destination — there should never be any.</summary>
    private string[] PartialFiles() =>
        Directory.Exists(_destination)
            ? [.. Directory.GetFiles(_destination, "*.part", SearchOption.AllDirectories)]
            : [];

    // ------------------------------------------------------- destination setup

    [Fact]
    public void Copy_CreatesDestinationFolder_WhenItDoesNotExist()
    {
        Assert.False(Directory.Exists(_destination));

        FileCopier.Copy([], _destination);

        Assert.True(Directory.Exists(_destination));
    }

    [Fact]
    public void Copy_CreatesNestedDestinationFolders()
    {
        string nested = Path.Combine(_destination, "2026", "09", "photos");

        FileCopier.Copy([], nested);

        Assert.True(Directory.Exists(nested));
    }

    // ------------------------------------------------------------- happy path

    [Fact]
    public void Copy_EmptySequence_ReturnsZeroedResult()
    {
        CopyResult result = FileCopier.Copy([], _destination);

        Assert.Equal(0, result.Copied);
        Assert.Equal(0, result.Skipped);
        Assert.Equal(0, result.BytesCopied);
        Assert.Empty(result.Errors);
    }

    [Fact]
    public void Copy_SingleFile_LeavesTheOriginalAndCreatesTheCopy()
    {
        string path = WriteFile(_source, "holiday.jpg", "bytes");

        CopyResult result = FileCopier.Copy([Scan(path)], _destination);

        Assert.Equal(1, result.Copied);
        Assert.Equal(0, result.Skipped);
        Assert.Empty(result.Errors);
        Assert.Equal("bytes", File.ReadAllText(path));
        Assert.Equal("bytes", File.ReadAllText(Path.Combine(_destination, "holiday.jpg")));
    }

    [Fact]
    public void Copy_PreservesFileContentByteForByte()
    {
        byte[] payload = [0xFF, 0xD8, 0xFF, 0xE0, 0x00, 0x10, 0x4A, 0x46];
        string path = Path.Combine(_source, "raw.jpg");
        File.WriteAllBytes(path, payload);

        FileCopier.Copy([Scan(path)], _destination);

        Assert.Equal(payload, File.ReadAllBytes(Path.Combine(_destination, "raw.jpg")));
    }

    /// <summary>
    /// Three megabytes is several turns of the one-megabyte buffer, which is the only way to catch
    /// a copy loop that drops or repeats a chunk — the shape of bug a 100 GB video would expose.
    /// </summary>
    [Fact]
    public void Copy_FileLargerThanTheBuffer_CopiesEveryByte()
    {
        byte[] payload = new byte[3 * 1024 * 1024];
        Random.Shared.NextBytes(payload);
        string path = Path.Combine(_source, "big.mp4");
        File.WriteAllBytes(path, payload);

        CopyResult result = FileCopier.Copy([Scan(path)], _destination);

        Assert.Equal(1, result.Copied);
        Assert.Equal(payload.Length, result.BytesCopied);
        Assert.Equal(payload, File.ReadAllBytes(Path.Combine(_destination, "big.mp4")));
    }

    /// <summary>
    /// The copy reserves the whole length up front so a 100 GB video is not laid down across
    /// thousands of fragments, and a length that is an exact multiple of the buffer is where that
    /// reservation is most easily left behind as trailing zeroes. Both sides of every boundary,
    /// therefore: the copy has to end at exactly the source's own length.
    /// </summary>
    [Theory]
    [InlineData((1024 * 1024) - 1)]
    [InlineData(1024 * 1024)]
    [InlineData((1024 * 1024) + 1)]
    [InlineData(2 * 1024 * 1024)]
    public void Copy_FileSizedAroundTheBuffer_LandsAtExactlyItsOwnLength(int length)
    {
        byte[] payload = new byte[length];
        Random.Shared.NextBytes(payload);
        string path = Path.Combine(_source, "clip.mp4");
        File.WriteAllBytes(path, payload);

        CopyResult result = FileCopier.Copy([Scan(path)], _destination);

        string copy = Path.Combine(_destination, "clip.mp4");
        Assert.Equal(length, new FileInfo(copy).Length);
        Assert.Equal(length, result.BytesCopied);
        Assert.Equal(payload, File.ReadAllBytes(copy));
    }

    /// <summary>
    /// The scan's length is a snapshot, and a phone can still be finalising a long video while its
    /// folder is being enumerated. The copy streams what is actually on disk, so the file lands
    /// whole even though the figure the scan carried was short.
    /// </summary>
    [Fact]
    public void Copy_FileThatGrewSinceTheScan_CopiesItsRealContent()
    {
        string path = WriteFile(_source, "recording.mp4", "start");
        ScannedFile stale = Scan(path);
        File.WriteAllText(path, "start and a good deal more");

        CopyResult result = FileCopier.Copy([stale], _destination);

        Assert.Equal(1, result.Copied);
        Assert.Equal(26, result.BytesCopied);
        Assert.Equal("start and a good deal more",
            File.ReadAllText(Path.Combine(_destination, "recording.mp4")));
    }

    [Fact]
    public void Copy_ManyFiles_CopiesEveryOne()
    {
        ScannedFile[] files = [.. Enumerable.Range(0, 50)
            .Select(i => Scan(WriteFile(_source, $"img-{i:D3}.jpg", i.ToString())))];

        CopyResult result = FileCopier.Copy(files, _destination);

        Assert.Equal(50, result.Copied);
        Assert.Empty(result.Errors);
        Assert.Equal(50, Directory.GetFiles(_source).Length);
        Assert.Equal(50, Directory.GetFiles(_destination).Length);
    }

    [Fact]
    public void Copy_GathersFilesFromSeveralSourceFolders()
    {
        string a = WriteFile(Path.Combine(_source, "camera"), "a.jpg");
        string b = WriteFile(Path.Combine(_source, "downloads"), "b.mp4");

        CopyResult result = FileCopier.Copy([Scan(a), Scan(b)], _destination);

        Assert.Equal(2, result.Copied);
        Assert.Equal(["a.jpg", "b.mp4"], DestinationNames());
    }

    [Fact]
    public void Copy_EmptyFile_IsCopiedLikeAnyOther()
    {
        string path = WriteFile(_source, "empty.mp4", string.Empty);

        CopyResult result = FileCopier.Copy([Scan(path)], _destination);

        Assert.Equal(1, result.Copied);
        Assert.Equal(0, new FileInfo(Path.Combine(_destination, "empty.mp4")).Length);
    }

    [Fact]
    public void Copy_TalliesTheBytesItWrote()
    {
        ScannedFile first = Scan(WriteFile(_source, "a.jpg", "12345"));
        ScannedFile second = Scan(WriteFile(_source, "b.jpg", "123"));

        CopyResult result = FileCopier.Copy([first, second], _destination);

        Assert.Equal(8, result.BytesCopied);
    }

    [Fact]
    public void Copy_IgnoresModifiedAndKindMetadata()
    {
        string path = WriteFile(_source, "mismatched.jpg", "real content");

        // Deliberately bogus: the copier reads Path, Name and Length, and nothing else.
        ScannedFile stale = new(path, "mismatched.jpg", Length: 12, Modified: DateTime.MinValue, MediaKind.Video);

        CopyResult result = FileCopier.Copy([stale], _destination);

        Assert.Equal(1, result.Copied);
        Assert.Equal("real content", File.ReadAllText(Path.Combine(_destination, "mismatched.jpg")));
    }

    [Fact]
    public void Copy_UnicodeAndSpacedNames_SurviveIntact()
    {
        string path = WriteFile(_source, "wakacje – Kraków 2026 🌞.jpg");

        CopyResult result = FileCopier.Copy([Scan(path)], _destination);

        Assert.Equal(1, result.Copied);
        Assert.True(File.Exists(Path.Combine(_destination, "wakacje – Kraków 2026 🌞.jpg")));
    }

    /// <summary>
    /// A stream copy, unlike File.Copy, writes a brand-new file — so the date the camera stamped on
    /// the original has to be carried over deliberately, or the copy would be dated today.
    /// </summary>
    [Fact]
    public void Copy_CarriesTheLastWriteTimeOver()
    {
        string path = WriteFile(_source, "stamped.jpg");
        DateTime stamped = File.GetLastWriteTimeUtc(path);

        FileCopier.Copy([Scan(path)], _destination);

        Assert.Equal(stamped, File.GetLastWriteTimeUtc(Path.Combine(_destination, "stamped.jpg")));
    }

    [Fact]
    public void Copy_LeavesNoPartialFileBehind()
    {
        ScannedFile file = Scan(WriteFile(_source, "done.jpg"));

        FileCopier.Copy([file], _destination);

        Assert.Empty(PartialFiles());
    }

    // ------------------------------------------------------------- second run

    /// <summary>
    /// The originals stay put, so nothing stops a second run from copying the whole library again.
    /// Matching name and length is what makes running the app twice settle instead of double it.
    /// </summary>
    [Fact]
    public void Copy_RunTwice_CopiesNothingTheSecondTime()
    {
        ScannedFile[] files =
        [
            Scan(WriteFile(_source, "a.jpg", "first")),
            Scan(WriteFile(_source, "b.jpg", "second one")),
        ];

        FileCopier.Copy(files, _destination);
        CopyResult second = FileCopier.Copy(files, _destination);

        Assert.Equal(0, second.Copied);
        Assert.Equal(2, second.Skipped);
        Assert.Empty(second.Errors);
        Assert.Equal(["a.jpg", "b.jpg"], DestinationNames());
    }

    [Fact]
    public void Copy_SameNameDifferentLength_GetsANumberedSuffix()
    {
        WriteFile(_destination, "clash.jpg", "existing file");
        string incoming = WriteFile(_source, "clash.jpg", "incoming");

        CopyResult result = FileCopier.Copy([Scan(incoming)], _destination);

        Assert.Equal(1, result.Copied);
        Assert.Equal("existing file", File.ReadAllText(Path.Combine(_destination, "clash.jpg")));
        Assert.Equal("incoming", File.ReadAllText(Path.Combine(_destination, "clash (1).jpg")));
    }

    /// <summary>
    /// A second run over a library whose first run had to suffix a name must recognise the suffixed
    /// copy, not add "(2)" beside it every time.
    /// </summary>
    [Fact]
    public void Copy_AlreadySuffixedCopyOfTheSameLength_IsSkipped()
    {
        WriteFile(_destination, "IMG_0001.jpg", "a different photo");
        string incoming = WriteFile(_source, "IMG_0001.jpg", "mine");
        WriteFile(_destination, "IMG_0001 (1).jpg", "mine");

        CopyResult result = FileCopier.Copy([Scan(incoming)], _destination);

        Assert.Equal(0, result.Copied);
        Assert.Equal(1, result.Skipped);
        Assert.Equal(["IMG_0001 (1).jpg", "IMG_0001.jpg"], DestinationNames());
    }

    /// <summary>
    /// The deliberate rough edge of matching on length: two different photos that happen to share a
    /// name and a byte count look like the same file. Reading both to be sure would mean re-reading
    /// the whole library on every run, which is the cost the rule exists to avoid.
    /// </summary>
    [Fact]
    public void Copy_SameNameAndLengthButDifferentContent_IsSkipped()
    {
        WriteFile(_destination, "twin.jpg", "aaaa");
        string incoming = WriteFile(_source, "twin.jpg", "bbbb");

        CopyResult result = FileCopier.Copy([Scan(incoming)], _destination);

        Assert.Equal(0, result.Copied);
        Assert.Equal(1, result.Skipped);
        Assert.Equal("aaaa", File.ReadAllText(Path.Combine(_destination, "twin.jpg")));
    }

    [Fact]
    public void Copy_SameFileListedTwice_CopiesOnceAndSkipsTheSecond()
    {
        ScannedFile file = Scan(WriteFile(_source, "twice.jpg"));

        CopyResult result = FileCopier.Copy([file, file], _destination);

        Assert.Equal(1, result.Copied);
        Assert.Equal(1, result.Skipped);
        Assert.Empty(result.Errors);
        Assert.Equal(["twice.jpg"], DestinationNames());
    }

    // ------------------------------------------------ already in destination

    [Fact]
    public void Copy_FileAlreadyInDestination_IsSkippedNotDuplicated()
    {
        string path = WriteFile(_destination, "in-place.jpg", "same");

        CopyResult result = FileCopier.Copy([Scan(path)], _destination);

        Assert.Equal(0, result.Copied);
        Assert.Equal(1, result.Skipped);
        Assert.Empty(result.Errors);
        Assert.Equal(["in-place.jpg"], DestinationNames());
        Assert.Equal("same", File.ReadAllText(path));
    }

    [Fact]
    public void Copy_SkipsInPlaceFilesButStillCopiesTheRest()
    {
        ScannedFile inPlace = Scan(WriteFile(_destination, "already.jpg"));
        ScannedFile incoming = Scan(WriteFile(_source, "incoming.jpg"));

        CopyResult result = FileCopier.Copy([inPlace, incoming], _destination);

        Assert.Equal(1, result.Copied);
        Assert.Equal(1, result.Skipped);
        Assert.Equal(["already.jpg", "incoming.jpg"], DestinationNames());
    }

    [Fact]
    public void Copy_NormalisesRelativeSourcePaths_BeforeTheInPlaceCheck()
    {
        WriteFile(_destination, "loop.jpg");

        // …/destination/../destination/loop.jpg is the very same file, just spelled awkwardly.
        string roundabout = Path.Combine(_destination, "..", "destination", "loop.jpg");

        CopyResult result = FileCopier.Copy(
            [new ScannedFile(roundabout, "loop.jpg", 0, DateTime.Now, MediaKind.Photo)], _destination);

        Assert.Equal(1, result.Skipped);
        Assert.Equal(0, result.Copied);
        Assert.Equal(["loop.jpg"], DestinationNames());
    }

    [Fact]
    public void Copy_SubfolderOfDestination_IsCopiedNotSkipped()
    {
        // A file one level down is *not* "already in the destination".
        string nested = WriteFile(Path.Combine(_destination, "inbox"), "nested.jpg");

        CopyResult result = FileCopier.Copy([Scan(nested)], _destination);

        Assert.Equal(1, result.Copied);
        Assert.Equal(0, result.Skipped);
        Assert.True(File.Exists(nested));
        Assert.Equal(["nested.jpg"], DestinationNames());
    }

    [Fact]
    public void Copy_FolderSharingAPrefixWithDestination_IsNotTreatedAsTheDestination()
    {
        // "destination-old" starts with "destination" but is a different folder entirely.
        string path = WriteFile(_root + Path.DirectorySeparatorChar + "destination-old", "prefix.jpg");

        CopyResult result = FileCopier.Copy([Scan(path)], _destination);

        Assert.Equal(1, result.Copied);
        Assert.Equal(0, result.Skipped);
        Assert.True(File.Exists(path));
    }

    // ------------------------------------------------------- name collisions

    [Fact]
    public void Copy_RepeatedClashes_KeepCountingUp()
    {
        WriteFile(_destination, "dup.jpg", "0");
        ScannedFile[] files =
        [
            Scan(WriteFile(Path.Combine(_source, "a"), "dup.jpg", "11")),
            Scan(WriteFile(Path.Combine(_source, "b"), "dup.jpg", "222")),
            Scan(WriteFile(Path.Combine(_source, "c"), "dup.jpg", "3333")),
        ];

        CopyResult result = FileCopier.Copy(files, _destination);

        Assert.Equal(3, result.Copied);
        Assert.Empty(result.Errors);
        Assert.Equal("11", File.ReadAllText(Path.Combine(_destination, "dup (1).jpg")));
        Assert.Equal("222", File.ReadAllText(Path.Combine(_destination, "dup (2).jpg")));
        Assert.Equal("3333", File.ReadAllText(Path.Combine(_destination, "dup (3).jpg")));
    }

    [Fact]
    public void Copy_SkipsSuffixesThatAreAlreadyTaken()
    {
        WriteFile(_destination, "gap.jpg", "0");
        WriteFile(_destination, "gap (1).jpg", "11");
        WriteFile(_destination, "gap (2).jpg", "222");
        string incoming = WriteFile(_source, "gap.jpg", "3333");

        FileCopier.Copy([Scan(incoming)], _destination);

        Assert.Equal("3333", File.ReadAllText(Path.Combine(_destination, "gap (3).jpg")));
    }

    [Fact]
    public void Copy_ClashWithADirectory_AlsoGetsASuffix()
    {
        // Nothing may be dropped on top of a folder that happens to share the name.
        Directory.CreateDirectory(Path.Combine(_destination, "album.jpg"));
        string incoming = WriteFile(_source, "album.jpg", "photo");

        CopyResult result = FileCopier.Copy([Scan(incoming)], _destination);

        Assert.Equal(1, result.Copied);
        Assert.True(Directory.Exists(Path.Combine(_destination, "album.jpg")));
        Assert.Equal("photo", File.ReadAllText(Path.Combine(_destination, "album (1).jpg")));
    }

    [Fact]
    public void Copy_DirectoryOccupyingTheSuffixedName_IsAlsoSteppedOver()
    {
        // The first candidate is taken by a file of another size and the " (1)" candidate by a
        // folder, so the only free slot is " (2)".
        WriteFile(_destination, "trip.jpg", "existing file");
        Directory.CreateDirectory(Path.Combine(_destination, "trip (1).jpg"));
        string incoming = WriteFile(_source, "trip.jpg", "incoming");

        CopyResult result = FileCopier.Copy([Scan(incoming)], _destination);

        Assert.Equal(1, result.Copied);
        Assert.True(Directory.Exists(Path.Combine(_destination, "trip (1).jpg")));
        Assert.Equal("incoming", File.ReadAllText(Path.Combine(_destination, "trip (2).jpg")));
    }

    [Fact]
    public void Copy_ClashOnAnExtensionlessName_SuffixesTheBareName()
    {
        WriteFile(_destination, "NOTES");
        string incoming = WriteFile(_source, "NOTES", "second");

        FileCopier.Copy([Scan(incoming)], _destination);

        Assert.Equal("second", File.ReadAllText(Path.Combine(_destination, "NOTES (1)")));
    }

    [Fact]
    public void Copy_ClashOnAMultiDotName_KeepsOnlyTheLastSegmentAsExtension()
    {
        WriteFile(_destination, "clip.2026-09-09.mp4");
        string incoming = WriteFile(_source, "clip.2026-09-09.mp4", "second");

        FileCopier.Copy([Scan(incoming)], _destination);

        Assert.Equal("second", File.ReadAllText(Path.Combine(_destination, "clip.2026-09-09 (1).mp4")));
    }

    [Fact]
    public void Copy_ClashOnADotPrefixedName_TreatsTheWholeNameAsExtension()
    {
        // Path.GetFileNameWithoutExtension(".hidden") is empty, so the suffix leads the name.
        WriteFile(_destination, ".hidden");
        string incoming = WriteFile(_source, ".hidden", "second");

        FileCopier.Copy([Scan(incoming)], _destination);

        Assert.Equal("second", File.ReadAllText(Path.Combine(_destination, " (1).hidden")));
    }

    [Fact]
    public void Copy_TwoDistinctFilesWithTheSameName_BothSurvive()
    {
        ScannedFile first = Scan(WriteFile(Path.Combine(_source, "phone"), "IMG_0001.jpg", "phone"));
        ScannedFile second = Scan(WriteFile(Path.Combine(_source, "camera"), "IMG_0001.jpg", "camera"));

        CopyResult result = FileCopier.Copy([first, second], _destination);

        Assert.Equal(2, result.Copied);
        Assert.Equal("phone", File.ReadAllText(Path.Combine(_destination, "IMG_0001.jpg")));
        Assert.Equal("camera", File.ReadAllText(Path.Combine(_destination, "IMG_0001 (1).jpg")));
    }

    [Fact]
    public void Copy_UsesTheScannedName_NotTheNameOnDisk()
    {
        string path = WriteFile(_source, "on-disk.jpg", "content");

        CopyResult result = FileCopier.Copy([Scan(path, name: "renamed.jpg")], _destination);

        Assert.Equal(1, result.Copied);
        Assert.Equal(["renamed.jpg"], DestinationNames());
    }

    // --------------------------------------------------------- error handling

    [Fact]
    public void Copy_MissingSourceFile_IsReportedAsAnError()
    {
        ScannedFile ghost = new(Path.Combine(_source, "ghost.jpg"), "ghost.jpg", 10, DateTime.Now, MediaKind.Photo);

        CopyResult result = FileCopier.Copy([ghost], _destination);

        Assert.Equal(0, result.Copied);
        Assert.Equal(0, result.Skipped);
        string error = Assert.Single(result.Errors);
        Assert.StartsWith("ghost.jpg: ", error);
        Assert.Empty(DestinationNames());
        Assert.Empty(PartialFiles());
    }

    [Fact]
    public void Copy_SourceIsADirectory_IsReportedAsAnError()
    {
        string folder = Path.Combine(_source, "not-a-file.jpg");
        Directory.CreateDirectory(folder);

        CopyResult result = FileCopier.Copy(
            [new ScannedFile(folder, "not-a-file.jpg", 0, DateTime.Now, MediaKind.Photo)], _destination);

        Assert.Equal(0, result.Copied);
        Assert.Single(result.Errors);
        Assert.True(Directory.Exists(folder));
        Assert.Empty(PartialFiles());
    }

    [Fact]
    public void Copy_BlankSourcePath_IsReportedAsAnError()
    {
        // Path.GetFullPath("") throws — the per-file try/catch has to absorb it.
        CopyResult result = FileCopier.Copy(
            [new ScannedFile("", "blank.jpg", 0, DateTime.Now, MediaKind.Photo)], _destination);

        Assert.Equal(0, result.Copied);
        Assert.StartsWith("blank.jpg: ", Assert.Single(result.Errors));
    }

    [Fact]
    public void Copy_NamePointingIntoAMissingSubfolder_IsReportedAsAnError()
    {
        string path = WriteFile(_source, "sub.jpg");
        ScannedFile file = Scan(path, name: Path.Combine("nope", "sub.jpg"));

        CopyResult result = FileCopier.Copy([file], _destination);

        Assert.Equal(0, result.Copied);
        Assert.Single(result.Errors);
        Assert.True(File.Exists(path));
    }

    [Fact]
    public void Copy_KeepsGoingAfterAFailure()
    {
        ScannedFile good1 = Scan(WriteFile(_source, "good1.jpg"));
        ScannedFile bad = new(Path.Combine(_source, "missing.jpg"), "missing.jpg", 0, DateTime.Now, MediaKind.Photo);
        ScannedFile good2 = Scan(WriteFile(_source, "good2.jpg"));

        CopyResult result = FileCopier.Copy([good1, bad, good2], _destination);

        Assert.Equal(2, result.Copied);
        Assert.Single(result.Errors);
        Assert.Equal(["good1.jpg", "good2.jpg"], DestinationNames());
    }

    [Fact]
    public void Copy_ReportsOneErrorPerFailingFile()
    {
        ScannedFile[] files = [.. Enumerable.Range(0, 3)
            .Select(i => new ScannedFile(
                Path.Combine(_source, $"missing{i}.jpg"), $"missing{i}.jpg", 0, DateTime.Now, MediaKind.Photo))];

        CopyResult result = FileCopier.Copy(files, _destination);

        Assert.Equal(3, result.Errors.Count);
        Assert.All(result.Errors, e => Assert.Contains(": ", e));
    }

    [Fact]
    public void Copy_DestinationIsAnExistingFile_ThrowsBeforeTouchingAnything()
    {
        string blocker = WriteFile(_root, "destination", "I am a file");
        ScannedFile file = Scan(WriteFile(_source, "any.jpg"));

        Assert.ThrowsAny<IOException>(() => FileCopier.Copy([file], blocker));

        Assert.True(File.Exists(Path.Combine(_source, "any.jpg")));
    }

    [Fact]
    public void Copy_BlankDestination_Throws()
    {
        Assert.ThrowsAny<ArgumentException>(() => FileCopier.Copy([], ""));
    }

    [Fact]
    public void Copy_NullDestination_Throws()
    {
        Assert.ThrowsAny<ArgumentNullException>(() => FileCopier.Copy([], null!));
    }

    // ------------------------------------------------------------ cancellation

    [Fact]
    public void Copy_AlreadyCancelledToken_ThrowsBeforeCopyingAnything()
    {
        ScannedFile file = Scan(WriteFile(_source, "untouched.jpg"));
        using CancellationTokenSource cts = new();
        cts.Cancel();

        Assert.Throws<OperationCanceledException>(() => FileCopier.Copy([file], _destination, token: cts.Token));

        Assert.True(File.Exists(Path.Combine(_source, "untouched.jpg")));
        Assert.Empty(DestinationNames());
    }

    [Fact]
    public void Copy_CancelledMidRun_KeepsWhatItAlreadyCopied()
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

        Assert.Throws<OperationCanceledException>(
            () => FileCopier.Copy(Cancelling(), _destination, token: cts.Token));

        Assert.Equal(["first.jpg"], DestinationNames());
        Assert.Empty(PartialFiles());
    }

    [Fact]
    public void Copy_CancellationEscapesTheErrorCollector()
    {
        // The catch-all must not swallow the cancellation into the Errors list.
        ScannedFile file = Scan(WriteFile(_source, "one.jpg"));
        using CancellationTokenSource cts = new();
        cts.Cancel();

        OperationCanceledException ex =
            Assert.Throws<OperationCanceledException>(() => FileCopier.Copy([file], _destination, token: cts.Token));

        Assert.Equal(cts.Token, ex.CancellationToken);
    }

    /// <summary>
    /// The real cancel is not the tidy one between files: the user hits Anuluj while a 100 GB
    /// video is streaming, and if that file is the last of the run there is no further iteration
    /// left to notice the token. Routing the token in through targetFor puts the cancellation
    /// exactly where a mid-file Anuluj puts it — inside the try that collects per-file failures.
    /// </summary>
    [Fact]
    public void Copy_CancelledInsideTheLastFile_ThrowsInsteadOfCollectingAnError()
    {
        ScannedFile file = Scan(WriteFile(_source, "clip.mp4", "several bytes of video"));
        using CancellationTokenSource cts = new();

        CopyTarget CancelWhileStreaming(ScannedFile scanned)
        {
            cts.Cancel();
            return new CopyTarget(DestinationLayout.SubfolderFor(null, scanned.Kind), false);
        }

        Assert.Throws<OperationCanceledException>(
            () => FileCopier.Copy([file], _destination, CancelWhileStreaming, cts.Token));

        Assert.True(File.Exists(file.Path));
        Assert.Empty(PartialFiles());
        Assert.False(File.Exists(Dated(DestinationLayout.UndatedFolder, DestinationLayout.PhotoFolder, "clip.mp4")));
    }

    [Fact]
    public void Copy_CancelledInsideAFile_DoesNotCountItAsCopied()
    {
        ScannedFile first = Scan(WriteFile(_source, "first.jpg", "12345"));
        ScannedFile second = Scan(WriteFile(_source, "second.jpg", "67890"));
        using CancellationTokenSource cts = new();
        Recorder recorder = new();

        CopyTarget CancelOnTheSecond(ScannedFile scanned)
        {
            if (scanned.Name == "second.jpg")
            {
                cts.Cancel();
            }

            return new CopyTarget(DestinationLayout.SubfolderFor(null, scanned.Kind), false);
        }

        Assert.Throws<OperationCanceledException>(
            () => FileCopier.Copy([first, second], _destination, CancelOnTheSecond, cts.Token, recorder));

        Assert.NotEmpty(recorder.Reports);
        // Five bytes is the first file alone, in both figures: the second was deleted with its
        // partial, so it neither reached the disk nor counts as work the run got through.
        Assert.Equal(new CopyProgress(1, 5, 5, "second.jpg"), recorder.Reports[^1]);
        Assert.Empty(PartialFiles());
    }

    [Fact]
    public void Copy_DefaultToken_NeverCancels()
    {
        ScannedFile file = Scan(WriteFile(_source, "plain.jpg"));

        CopyResult result = FileCopier.Copy([file], _destination);

        Assert.Equal(1, result.Copied);
    }

    // --------------------------------------------------------------- progress

    /// <summary>Records every report so a test can look at what the window would have shown.</summary>
    private sealed class Recorder : IProgress<CopyProgress>
    {
        public List<CopyProgress> Reports { get; } = [];

        public void Report(CopyProgress value) => Reports.Add(value);
    }

    /// <summary>
    /// Reports are throttled, so what a run guarantees is its last one: the finished tally, which
    /// is what the window leaves on the progress bar when the copy ends.
    /// </summary>
    [Fact]
    public void Copy_ReportsTheFinishedTally_WhenTheRunEnds()
    {
        ScannedFile first = Scan(WriteFile(_source, "first.jpg", "12345"));
        ScannedFile second = Scan(WriteFile(_source, "second.jpg", "123"));
        Recorder recorder = new();

        FileCopier.Copy([first, second], _destination, progress: recorder);

        Assert.NotEmpty(recorder.Reports);
        Assert.Equal(new CopyProgress(2, 8, 8, "second.jpg"), recorder.Reports[^1]);
    }

    /// <summary>
    /// The throttle is the whole point of the interval: a hundred thousand small files must not
    /// post a hundred thousand updates at the UI thread. Two hundred files of a few bytes each
    /// copy in single-digit milliseconds — two orders of magnitude inside the 200 ms interval —
    /// so the bound below is not a race against the clock.
    /// </summary>
    [Fact]
    public void Copy_ManySmallFiles_PostFarFewerReportsThanThereAreFiles()
    {
        List<ScannedFile> files =
            [.. Enumerable.Range(0, 200).Select(i => Scan(WriteFile(_source, $"shot{i}.jpg", $"payload {i}")))];
        Recorder recorder = new();

        CopyResult result = FileCopier.Copy(files, _destination, progress: recorder);

        Assert.Equal(200, result.Copied);
        Assert.InRange(recorder.Reports.Count, 1, 20);
    }

    /// <summary>
    /// A second run over a sorted library copies nothing at all, and used to report nothing with
    /// it — which left the window's bar sitting at nought while the run did its whole job. What it
    /// settles is the work it got through, so the bar fills even though no byte was written.
    /// </summary>
    [Fact]
    public void Copy_EverythingSkipped_StillReportsTheRunAsFinished()
    {
        ScannedFile file = Scan(WriteFile(_destination, "in-place.jpg", "already here"));
        Recorder recorder = new();

        FileCopier.Copy([file], _destination, progress: recorder);

        CopyProgress last = recorder.Reports[^1];
        Assert.Equal(0, last.BytesDone);
        Assert.Equal(file.Length, last.BytesSettled);
    }

    [Fact]
    public void Copy_EmptySequence_ReportsNothingAtAll()
    {
        Recorder recorder = new();

        FileCopier.Copy([], _destination, progress: recorder);

        Assert.Empty(recorder.Reports);
    }

    [Fact]
    public void Copy_WithoutAProgressSink_StillCopies()
    {
        ScannedFile file = Scan(WriteFile(_source, "quiet.jpg"));

        Assert.Equal(1, FileCopier.Copy([file], _destination, progress: null).Copied);
    }

    /// <summary>
    /// The figure behind the progress bar has to reach the scan's own total, or the bar stops
    /// short of the end on a run that did everything asked of it. A file that failed counts
    /// towards it just as a copied one does — the run is past it either way, and leaving it out
    /// would park the bar at 99.99% for one bad file in a hundred thousand.
    /// </summary>
    [Fact]
    public void Copy_SettlesEveryFile_WhetherItCopiedSkippedOrFailed()
    {
        ScannedFile copies = Scan(WriteFile(_source, "incoming.jpg", "12345"));
        ScannedFile inPlace = Scan(WriteFile(_destination, "in-place.jpg", "already here"));
        ScannedFile missing = Scan(Path.Combine(_source, "gone.jpg")) with { Length = 7 };
        Recorder recorder = new();

        CopyResult result = FileCopier.Copy([copies, inPlace, missing], _destination, progress: recorder);

        Assert.Equal(1, result.Copied);
        Assert.Equal(1, result.Skipped);
        Assert.Single(result.Errors);
        Assert.Equal(copies.Length + inPlace.Length + missing.Length, recorder.Reports[^1].BytesSettled);
    }

    /// <summary>
    /// The scan's length is what the window turns into a percentage, so the settled figure is
    /// snapped to it rather than accumulated: a video still being finalised when the folder was
    /// enumerated must not push the bar past its own end.
    /// </summary>
    [Fact]
    public void Copy_FileThatGrewSinceTheScan_SettlesAtItsScannedLength()
    {
        string path = WriteFile(_source, "recording.mp4", "start");
        ScannedFile stale = Scan(path);
        File.WriteAllText(path, "start and a good deal more");
        Recorder recorder = new();

        CopyResult result = FileCopier.Copy([stale], _destination, progress: recorder);

        Assert.Equal(26, result.BytesCopied);
        Assert.Equal(5, recorder.Reports[^1].BytesSettled);
    }

    // -------------------------------------------------------------- lazy input

    [Fact]
    public void Copy_EnumeratesTheSequenceLazilyAndExactlyOnce()
    {
        int enumerations = 0;
        ScannedFile file = Scan(WriteFile(_source, "lazy.jpg"));

        IEnumerable<ScannedFile> Counting()
        {
            enumerations++;
            yield return file;
        }

        CopyResult result = FileCopier.Copy(Counting(), _destination);

        Assert.Equal(1, enumerations);
        Assert.Equal(1, result.Copied);
    }

    [Fact]
    public void Copy_NullSequence_ThrowsAfterCreatingTheDestination()
    {
        // No argument guard: the foreach dereferences null, and only after the
        // destination folder has already been created as a side effect.
        Assert.Throws<NullReferenceException>(() => FileCopier.Copy(null!, _destination));
        Assert.True(Directory.Exists(_destination));
    }

    // ------------------------------------------------ destination path shapes

    /// <summary>
    /// A folder picker or a settings round-trip can easily hand over "…/destination/".
    /// GetFullPath keeps that trailing separator while GetDirectoryName never emits one,
    /// so the in-place check has to normalise before comparing.
    /// </summary>
    [Fact]
    public void Copy_DestinationWithTrailingSeparator_StillSkipsInPlaceFiles()
    {
        string path = WriteFile(_destination, "in-place.jpg", "same");

        CopyResult result = FileCopier.Copy([Scan(path)], _destination + Path.DirectorySeparatorChar);

        Assert.Equal(1, result.Skipped);
        Assert.Equal(0, result.Copied);
        Assert.Equal(["in-place.jpg"], DestinationNames());
        Assert.Equal("same", File.ReadAllText(path));
    }

    [Fact]
    public void Copy_DestinationWithTrailingSeparator_StillCopiesIncomingFiles()
    {
        ScannedFile file = Scan(WriteFile(_source, "incoming.jpg"));

        CopyResult result = FileCopier.Copy([file], _destination + Path.DirectorySeparatorChar);

        Assert.Equal(1, result.Copied);
        Assert.Equal(["incoming.jpg"], DestinationNames());
    }

    [Fact]
    public void Copy_DestinationWithRedundantSegments_StillSkipsInPlaceFiles()
    {
        string path = WriteFile(_destination, "in-place.jpg", "same");
        string roundabout = Path.Combine(_destination, "..", "destination");

        CopyResult result = FileCopier.Copy([Scan(path)], roundabout);

        Assert.Equal(1, result.Skipped);
        Assert.Equal(0, result.Copied);
        Assert.Equal(["in-place.jpg"], DestinationNames());
    }

    // ------------------------------------------------- orphaned partial files
    //
    // A cancel or a failure deletes its own partial. A killed process cannot, and the orphan is
    // invisible to the scanner afterwards — ".part" is not a media extension — so a run sweeps
    // the folders it visits.

    /// <summary>Writes a file that looks like the wreckage of a run that was killed mid-copy.</summary>
    private string WriteOrphanedPartial(string folder, string name) =>
        WriteFile(folder, name, "half a video");

    [Fact]
    public void Copy_OrphanedPartialInTheDestination_IsSwept()
    {
        WriteOrphanedPartial(_destination, "killed.mp4.part");
        ScannedFile file = Scan(WriteFile(_source, "incoming.jpg"));

        FileCopier.Copy([file], _destination);

        Assert.Equal(["incoming.jpg"], DestinationNames());
    }

    [Fact]
    public void Copy_OrphanedPartialInADatedFolder_IsSwept()
    {
        WriteOrphanedPartial(Dated("2026_03_01", "Zdjęcia"), "killed.mp4.part");
        ScannedFile file = Scan(WriteFile(_source, "incoming.jpg"));

        FileCopier.Copy([file], _destination, RouteTo(new DateTime(2026, 3, 1)));

        Assert.Empty(PartialFiles());
        Assert.True(File.Exists(Dated("2026_03_01", "Zdjęcia", "incoming.jpg")));
    }

    /// <summary>
    /// The sweep is per folder, on first sight, so a partial in a dated folder this run never
    /// routes anything into stays where it is. Widening it to the whole tree would mean a
    /// recursive walk of a sorted library before every run.
    /// </summary>
    [Fact]
    public void Copy_OrphanedPartialInAFolderTheRunNeverVisits_IsLeftAlone()
    {
        string untouched = WriteOrphanedPartial(Dated("2019_01_01", "Wideo"), "killed.mp4.part");
        ScannedFile file = Scan(WriteFile(_source, "incoming.jpg"));

        FileCopier.Copy([file], _destination, RouteTo(new DateTime(2026, 3, 1)));

        Assert.True(File.Exists(untouched));
    }

    /// <summary>
    /// Only a name that ends in ".part" is wreckage. One that merely contains it is somebody's
    /// file — and on Windows a "*.part" search pattern would have matched more besides.
    /// </summary>
    [Fact]
    public void Copy_FileMerelyContainingPartInItsName_IsNotSwept()
    {
        string bystander = WriteFile(_destination, "a.part.jpg", "a real photo");
        ScannedFile file = Scan(WriteFile(_source, "incoming.jpg"));

        FileCopier.Copy([file], _destination);

        Assert.True(File.Exists(bystander));
    }

    [Fact]
    public void Copy_SweepingAPartial_DoesNotDisturbTheFileItWasNamedAfter()
    {
        string whole = WriteFile(_destination, "clip.mp4", "the finished copy");
        WriteOrphanedPartial(_destination, "clip.mp4.part");
        ScannedFile file = Scan(WriteFile(_source, "incoming.jpg"));

        FileCopier.Copy([file], _destination);

        Assert.Equal("the finished copy", File.ReadAllText(whole));
        Assert.Empty(PartialFiles());
    }

    // ------------------------------------------------------------- free space

    [Fact]
    public void RoomFor_AByteOnARealFolder_Fits()
    {
        SpaceCheck space = FileCopier.RoomFor(_root, 1);

        Assert.True(space.Fits);
        Assert.Equal(1, space.Required);
    }

    [Fact]
    public void RoomFor_MoreThanAnyVolumeHolds_DoesNotFit()
    {
        SpaceCheck space = FileCopier.RoomFor(_root, long.MaxValue);

        Assert.False(space.Fits);
    }

    /// <summary>
    /// An unknown figure has to read as "fits". The real case is a UNC destination, which DriveInfo
    /// will not measure on Windows — it cannot be asserted here, because a backslash path is an
    /// ordinary relative name on macOS and resolves to a perfectly measurable volume. A blank path
    /// reaches the same branch on either platform, which is what this pins down.
    /// </summary>
    [Fact]
    public void RoomFor_AVolumeItCannotMeasure_SaysSoAndFits()
    {
        SpaceCheck space = FileCopier.RoomFor(string.Empty, long.MaxValue);

        Assert.Null(space.Available);
        Assert.True(space.Fits);
    }

    // ------------------------------------------------------------- routing
    //
    // Everything above exercises the flat default. These cases hand Copy a targetFor
    // delegate and check the dated tree it builds underneath the destination.

    /// <summary>Routes by the date and place the test names, so no metadata is involved.</summary>
    private static Func<ScannedFile, CopyTarget> RouteTo(DateTime? taken, string? place = null) =>
        file => new CopyTarget(DestinationLayout.SubfolderFor(taken, file.Kind, place), taken is not null);

    private string Dated(params string[] segments) => Path.Combine([_destination, .. segments]);

    [Fact]
    public void Copy_CreatesTheDatedFolderChain_WhenItDoesNotExist()
    {
        ScannedFile file = Scan(WriteFile(_source, "incoming.jpg"));

        CopyResult result = FileCopier.Copy([file], _destination, RouteTo(new DateTime(2026, 3, 1)));

        Assert.Equal(1, result.Copied);
        Assert.True(File.Exists(Dated("2026_03_01", "Zdjęcia", "incoming.jpg")));
        Assert.Empty(DestinationNames());
    }

    [Fact]
    public void Copy_PutsThePlaceBelowTheMediaKind()
    {
        ScannedFile photo = Scan(WriteFile(_source, "shot.jpg"));
        ScannedFile video = Scan(WriteFile(_source, "clip.mp4")) with { Kind = MediaKind.Video };

        FileCopier.Copy([photo, video], _destination, RouteTo(new DateTime(2026, 3, 1), "Zakopane"));

        Assert.True(File.Exists(Dated("2026_03_01", "Zdjęcia", "Zakopane", "shot.jpg")));
        Assert.True(File.Exists(Dated("2026_03_01", "Wideo", "Zakopane", "clip.mp4")));
    }

    [Fact]
    public void Copy_SendsTwoPlacesOfOneDayToTwoFolders()
    {
        ScannedFile here = Scan(WriteFile(_source, "here.jpg"));
        ScannedFile there = Scan(WriteFile(_source, "there.jpg"));

        FileCopier.Copy([here], _destination, RouteTo(new DateTime(2026, 3, 1), "Kraków"));
        FileCopier.Copy([there], _destination, RouteTo(new DateTime(2026, 3, 1), "Gdańsk"));

        Assert.True(File.Exists(Dated("2026_03_01", "Zdjęcia", "Kraków", "here.jpg")));
        Assert.True(File.Exists(Dated("2026_03_01", "Zdjęcia", "Gdańsk", "there.jpg")));
    }

    [Fact]
    public void Copy_ReusesAnExistingDatedFolder_InsteadOfDuplicatingIt()
    {
        WriteFile(Dated("2026_03_01", "Zdjęcia"), "already-here.jpg", "old");
        ScannedFile file = Scan(WriteFile(_source, "incoming.jpg"));

        FileCopier.Copy([file], _destination, RouteTo(new DateTime(2026, 3, 1)));

        Assert.Equal(
            ["already-here.jpg", "incoming.jpg"],
            Directory.GetFiles(Dated("2026_03_01", "Zdjęcia"))
                .Select(Path.GetFileName)
                .OrderBy(n => n, StringComparer.Ordinal));

        Assert.Equal(["2026_03_01"], Directory.GetDirectories(_destination).Select(Path.GetFileName));
    }

    [Fact]
    public void Copy_SplitsPhotosAndVideosOfTheSameDay()
    {
        ScannedFile photo = Scan(WriteFile(_source, "shot.jpg"));
        ScannedFile video = Scan(WriteFile(_source, "clip.mp4")) with { Kind = MediaKind.Video };

        FileCopier.Copy([photo, video], _destination, RouteTo(new DateTime(2026, 3, 1)));

        Assert.True(File.Exists(Dated("2026_03_01", "Zdjęcia", "shot.jpg")));
        Assert.True(File.Exists(Dated("2026_03_01", "Wideo", "clip.mp4")));
    }

    [Fact]
    public void Copy_RoutesUndatedMediaToBezDaty_AndCountsThem()
    {
        ScannedFile photo = Scan(WriteFile(_source, "blob.jpg"));
        ScannedFile video = Scan(WriteFile(_source, "blob.mp4")) with { Kind = MediaKind.Video };

        CopyResult result = FileCopier.Copy([photo, video], _destination, RouteTo(null));

        Assert.Equal(2, result.Copied);
        Assert.Equal(2, result.Undated);
        Assert.True(File.Exists(Dated("Bez daty", "Zdjęcia", "blob.jpg")));
        Assert.True(File.Exists(Dated("Bez daty", "Wideo", "blob.mp4")));
    }

    [Fact]
    public void Copy_CountsOnlyUndatedFiles()
    {
        ScannedFile dated = Scan(WriteFile(_source, "dated.jpg"));

        CopyResult result = FileCopier.Copy([dated], _destination, RouteTo(new DateTime(2026, 3, 1)));

        Assert.Equal(1, result.Copied);
        Assert.Equal(0, result.Undated);
    }

    [Fact]
    public void Copy_DoesNotCountUndatedFilesItSkipped()
    {
        // Otherwise a re-run could report "copied 0 · undated 1", which reads as nonsense.
        string path = WriteFile(Dated("Bez daty", "Zdjęcia"), "settled.jpg");

        CopyResult result = FileCopier.Copy([Scan(path)], _destination, RouteTo(null));

        Assert.Equal(1, result.Skipped);
        Assert.Equal(0, result.Copied);
        Assert.Equal(0, result.Undated);
    }

    [Fact]
    public void Copy_ReportsNoUndated_WhenRoutingIsNotUsed()
    {
        ScannedFile file = Scan(WriteFile(_source, "incoming.jpg"));

        Assert.Equal(0, FileCopier.Copy([file], _destination).Undated);
    }

    /// <summary>Re-running over an already-sorted tree must settle, not churn.</summary>
    [Fact]
    public void Copy_SkipsAFileAlreadySittingInItsDatedFolder()
    {
        string path = WriteFile(Dated("2026_03_01", "Zdjęcia"), "settled.jpg", "same");

        CopyResult result = FileCopier.Copy([Scan(path)], _destination, RouteTo(new DateTime(2026, 3, 1)));

        Assert.Equal(1, result.Skipped);
        Assert.Equal(0, result.Copied);
        Assert.Equal("same", File.ReadAllText(path));
    }

    /// <summary>The whole run, twice, over the tree the app actually builds.</summary>
    [Fact]
    public void Copy_IntoTheDatedTreeTwice_IsANoOpTheSecondTime()
    {
        ScannedFile photo = Scan(WriteFile(_source, "shot.jpg", "one"));
        ScannedFile video = Scan(WriteFile(_source, "clip.mp4", "two two")) with { Kind = MediaKind.Video };
        Func<ScannedFile, CopyTarget> route = RouteTo(new DateTime(2026, 3, 1), "Warszawa");

        FileCopier.Copy([photo, video], _destination, route);
        CopyResult second = FileCopier.Copy([photo, video], _destination, route);

        Assert.Equal(0, second.Copied);
        Assert.Equal(2, second.Skipped);
        Assert.Empty(Directory.GetFiles(_destination, "* (1)*", SearchOption.AllDirectories));
    }

    [Fact]
    public void Copy_CopiesAFileSittingInTheWrongDatedFolder()
    {
        string path = WriteFile(Dated("2025_01_05", "Zdjęcia"), "misfiled.jpg");

        CopyResult result = FileCopier.Copy([Scan(path)], _destination, RouteTo(new DateTime(2026, 3, 1)));

        Assert.Equal(1, result.Copied);
        Assert.True(File.Exists(path));
        Assert.True(File.Exists(Dated("2026_03_01", "Zdjęcia", "misfiled.jpg")));
    }

    [Fact]
    public void Copy_SuffixesANameClashInsideADatedFolder()
    {
        WriteFile(Dated("2026_03_01", "Zdjęcia"), "clash.jpg", "existing file");
        ScannedFile incoming = Scan(WriteFile(_source, "clash.jpg", "incoming"));

        FileCopier.Copy([incoming], _destination, RouteTo(new DateTime(2026, 3, 1)));

        string folder = Dated("2026_03_01", "Zdjęcia");
        Assert.Equal("existing file", File.ReadAllText(Path.Combine(folder, "clash.jpg")));
        Assert.Equal("incoming", File.ReadAllText(Path.Combine(folder, "clash (1).jpg")));
    }

    [Fact]
    public void Copy_SendsTwoDaysToTwoFolders()
    {
        ScannedFile march = Scan(WriteFile(_source, "march.jpg"));
        ScannedFile january = Scan(WriteFile(_source, "january.jpg"));

        FileCopier.Copy([march], _destination, RouteTo(new DateTime(2026, 3, 1)));
        FileCopier.Copy([january], _destination, RouteTo(new DateTime(2026, 1, 9)));

        Assert.True(File.Exists(Dated("2026_03_01", "Zdjęcia", "march.jpg")));
        Assert.True(File.Exists(Dated("2026_01_09", "Zdjęcia", "january.jpg")));
    }

    [Fact]
    public void Copy_RecordsAnErrorAndKeepsGoing_WhenRoutingThrows()
    {
        ScannedFile bad = Scan(WriteFile(_source, "bad.jpg"));
        ScannedFile good = Scan(WriteFile(_source, "good.jpg"));

        CopyResult result = FileCopier.Copy(
            [bad, good],
            _destination,
            file => file.Name == "bad.jpg"
                ? throw new InvalidOperationException("unreadable")
                : new CopyTarget(DestinationLayout.SubfolderFor(new DateTime(2026, 3, 1), file.Kind), true));

        Assert.Equal(1, result.Copied);
        Assert.Equal(["bad.jpg: unreadable"], result.Errors);
        Assert.True(File.Exists(Dated("2026_03_01", "Zdjęcia", "good.jpg")));
    }

    [Fact]
    public void Copy_WithRouting_StillLetsCancellationEscape()
    {
        ScannedFile file = Scan(WriteFile(_source, "incoming.jpg"));
        using CancellationTokenSource cts = new();
        cts.Cancel();

        Assert.Throws<OperationCanceledException>(() =>
            FileCopier.Copy([file], _destination, RouteTo(new DateTime(2026, 3, 1)), cts.Token));

        Assert.True(File.Exists(file.Path));
    }
}
