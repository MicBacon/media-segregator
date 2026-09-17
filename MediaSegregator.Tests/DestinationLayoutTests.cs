using System.Globalization;

namespace MediaSegregator.Tests;

/// <summary>
/// The folder tree is pure string work, so the SubfolderFor tests never touch a disk. Paths are
/// asserted against Path.Combine rather than a literal "2026_03_01/…" so they hold on Windows too.
///
/// TargetFor does touch a disk — it is the one place where the date reader, the location reader
/// and the place list meet — so the section at the bottom gives it a temp folder. What it covers
/// is the wiring, not the readers: a file carrying no recognisable metadata, which is what every
/// case here writes, still has to reach the right folder through the file-name fallback. The
/// readers themselves are covered byte by byte in MediaDateTests and MediaLocationTests, and the
/// tree-building in FileCopier is covered there with a hand-made delegate.
/// </summary>
public sealed class DestinationLayoutTests : IDisposable
{
    private readonly string _root;

    public DestinationLayoutTests()
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

    private static string Expected(params string[] segments) => Path.Combine(segments);

    // ------------------------------------------------------------------- shape

    [Fact]
    public void SubfolderFor_MatchesTheRequestedLayout()
    {
        Assert.Equal(
            Expected("2026_03_01", "Zdjęcia"),
            DestinationLayout.SubfolderFor(new DateTime(2026, 3, 1), MediaKind.Photo));

        Assert.Equal(
            Expected("2025_03_01", "Wideo"),
            DestinationLayout.SubfolderFor(new DateTime(2025, 3, 1), MediaKind.Video));
    }

    [Fact]
    public void SubfolderFor_PadsTheMonthAndDayToTwoDigits()
    {
        Assert.Equal(
            Expected("2026_01_09", "Zdjęcia"),
            DestinationLayout.SubfolderFor(new DateTime(2026, 1, 9), MediaKind.Photo));
    }

    [Fact]
    public void SubfolderFor_IgnoresTheTimeOfDay()
    {
        Assert.Equal(
            DestinationLayout.SubfolderFor(new DateTime(2026, 3, 1, 0, 0, 0), MediaKind.Photo),
            DestinationLayout.SubfolderFor(new DateTime(2026, 3, 1, 23, 59, 59), MediaKind.Photo));
    }

    [Fact]
    public void SubfolderFor_HandlesLeapDay()
    {
        Assert.Equal(
            Expected("2024_02_29", "Zdjęcia"),
            DestinationLayout.SubfolderFor(new DateTime(2024, 2, 29), MediaKind.Photo));
    }

    [Theory]
    [InlineData(2025, 12, 31, "2025_12_31")]
    [InlineData(2026, 1, 1, "2026_01_01")]
    public void SubfolderFor_HandlesYearBoundaries(int year, int month, int day, string expected)
    {
        Assert.Equal(
            Expected(expected, "Wideo"),
            DestinationLayout.SubfolderFor(new DateTime(year, month, day), MediaKind.Video));
    }

    [Fact]
    public void SubfolderFor_SeparatesWithThePlatformDirectoryCharacter()
    {
        string subfolder = DestinationLayout.SubfolderFor(new DateTime(2026, 3, 1), MediaKind.Photo, "Warszawa");

        Assert.Equal(["2026_03_01", "Zdjęcia", "Warszawa"], subfolder.Split(Path.DirectorySeparatorChar));
    }

    // ------------------------------------------------------------------- place

    [Fact]
    public void SubfolderFor_AddsThePlaceBelowTheMediaKind()
    {
        Assert.Equal(
            Expected("2026_03_01", "Zdjęcia", "Zakopane"),
            DestinationLayout.SubfolderFor(new DateTime(2026, 3, 1), MediaKind.Photo, "Zakopane"));

        Assert.Equal(
            Expected("2026_03_01", "Wideo", "Zakopane"),
            DestinationLayout.SubfolderFor(new DateTime(2026, 3, 1), MediaKind.Video, "Zakopane"));
    }

    /// <summary>
    /// Screenshots, WhatsApp media and older videos carry no coordinates, and most libraries are
    /// mostly those — a "Bez lokalizacji" folder beside every date would bury the ones that do.
    /// </summary>
    [Theory]
    [InlineData(null)]
    [InlineData("")]
    [InlineData("   ")]
    public void SubfolderFor_WithoutAPlace_StopsAtTheMediaKind(string? place)
    {
        Assert.Equal(
            Expected("2026_03_01", "Zdjęcia"),
            DestinationLayout.SubfolderFor(new DateTime(2026, 3, 1), MediaKind.Photo, place));
    }

    // ----------------------------------------------------------------- no date

    [Fact]
    public void SubfolderFor_RoutesUndatedMediaToBezDaty()
    {
        Assert.Equal(Expected("Bez daty", "Zdjęcia"), DestinationLayout.SubfolderFor(null, MediaKind.Photo));
        Assert.Equal(Expected("Bez daty", "Wideo"), DestinationLayout.SubfolderFor(null, MediaKind.Video));
    }

    [Fact]
    public void SubfolderFor_UndatedMediaStillGetsItsPlace()
    {
        Assert.Equal(
            Expected("Bez daty", "Zdjęcia", "Gdańsk"),
            DestinationLayout.SubfolderFor(null, MediaKind.Photo, "Gdańsk"));
    }

    // ----------------------------------------------------------------- culture

    [Fact]
    public void SubfolderFor_KeepsAsciiDigits_UnderALocaleThatDoesNot()
    {
        CultureInfo original = CultureInfo.CurrentCulture;

        try
        {
            // ar-EG renders numbers with Arabic-Indic digits when the current culture is used.
            CultureInfo.CurrentCulture = new CultureInfo("ar-EG");

            Assert.Equal(
                Expected("2026_03_01", "Zdjęcia"),
                DestinationLayout.SubfolderFor(new DateTime(2026, 3, 1), MediaKind.Photo));
        }
        finally
        {
            CultureInfo.CurrentCulture = original;
        }
    }

    // ------------------------------------------------------------------ wiring
    //
    // TargetFor over real files. None of them is a parseable photo or video, which is exactly the
    // case the file-name fallback exists for — and the case a phone's folder is full of once a
    // messaging app has been through it.

    /// <summary>A ScannedFile backed by a real, deliberately metadata-free file.</summary>
    private ScannedFile Plain(string name, MediaKind kind = MediaKind.Photo)
    {
        string path = Path.Combine(_root, name);
        File.WriteAllText(path, "not a container this reader knows");

        return new ScannedFile(path, name, new FileInfo(path).Length, File.GetLastWriteTime(path), kind);
    }

    [Fact]
    public void TargetFor_FallsBackToTheDateInTheName_WhenThereIsNoMetadata()
    {
        CopyTarget target = DestinationLayout.TargetFor(Plain("IMG_20260301_142233.jpg"));

        Assert.True(target.Dated);
        Assert.Equal(Expected("2026_03_01", "Zdjęcia"), target.Subfolder);
    }

    [Fact]
    public void TargetFor_KeepsTheMediaKindItWasScannedAs()
    {
        CopyTarget target = DestinationLayout.TargetFor(Plain("VID_20260301_142233.mp4", MediaKind.Video));

        Assert.Equal(Expected("2026_03_01", "Wideo"), target.Subfolder);
    }

    [Fact]
    public void TargetFor_FileWithNoDateAnywhere_IsReportedAsUndated()
    {
        CopyTarget target = DestinationLayout.TargetFor(Plain("holiday.jpg"));

        Assert.False(target.Dated);
        Assert.Equal(Expected(DestinationLayout.UndatedFolder, "Zdjęcia"), target.Subfolder);
    }

    /// <summary>
    /// The filesystem timestamp is today's, and today is a perfectly plausible capture date — so a
    /// reader that consulted it would send this file to a dated folder instead of Bez daty. This is
    /// the invariant at the layout level rather than at MediaDate's.
    /// </summary>
    [Fact]
    public void TargetFor_NeverRoutesByTheFileSystemTimestamp()
    {
        CopyTarget target = DestinationLayout.TargetFor(Plain("holiday.jpg"));

        Assert.Equal(Expected(DestinationLayout.UndatedFolder, DestinationLayout.PhotoFolder),
            target.Subfolder);
    }

    [Fact]
    public void TargetFor_MissingFile_IsUndatedRatherThanThrowing()
    {
        string name = "gone.jpg";
        ScannedFile file = new(Path.Combine(_root, name), name, 0, DateTime.Now, MediaKind.Photo);

        CopyTarget target = DestinationLayout.TargetFor(file);

        Assert.False(target.Dated);
        Assert.Equal(Expected(DestinationLayout.UndatedFolder, "Zdjęcia"), target.Subfolder);
    }

    /// <summary>
    /// A name-only date carries no coordinates with it, so there is no third segment: the place is
    /// omitted rather than guessed at or filled in with a coordinate label.
    /// </summary>
    [Fact]
    public void TargetFor_WithNoCoordinates_AddsNoPlaceSegment()
    {
        CopyTarget target = DestinationLayout.TargetFor(Plain("IMG_20260301_142233.jpg"));

        Assert.Equal(2, target.Subfolder.Split(Path.DirectorySeparatorChar).Length);
    }
}
