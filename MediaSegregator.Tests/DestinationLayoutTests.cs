using System.Globalization;

namespace MediaSegregator.Tests;

/// <summary>
/// The folder tree is pure string work, so these tests never touch a disk. Paths are asserted
/// against Path.Combine rather than a literal "2026/mar/01/…" so they hold on Windows too.
/// </summary>
public sealed class DestinationLayoutTests
{
    private static string Expected(params string[] segments) => Path.Combine(segments);

    // ------------------------------------------------------------------ months

    [Theory]
    [InlineData(1, "sty")]
    [InlineData(2, "lut")]
    [InlineData(3, "mar")]
    [InlineData(4, "kwi")]
    [InlineData(5, "maj")]
    [InlineData(6, "cze")]
    [InlineData(7, "lip")]
    [InlineData(8, "sie")]
    [InlineData(9, "wrz")]
    [InlineData(10, "paź")]
    [InlineData(11, "lis")]
    [InlineData(12, "gru")]
    public void SubfolderFor_UsesPolishMonthAbbreviation(int month, string expected)
    {
        string subfolder = DestinationLayout.SubfolderFor(new DateTime(2026, month, 15), MediaKind.Photo);

        Assert.Equal(Expected("2026", expected, "15", "Zdjęcia"), subfolder);
    }

    // ------------------------------------------------------------------- shape

    [Fact]
    public void SubfolderFor_MatchesTheRequestedLayout()
    {
        Assert.Equal(
            Expected("2026", "mar", "01", "Zdjęcia"),
            DestinationLayout.SubfolderFor(new DateTime(2026, 3, 1), MediaKind.Photo));

        Assert.Equal(
            Expected("2025", "mar", "01", "Wideo"),
            DestinationLayout.SubfolderFor(new DateTime(2025, 3, 1), MediaKind.Video));
    }

    [Fact]
    public void SubfolderFor_PadsTheDayToTwoDigits()
    {
        Assert.Equal(
            Expected("2026", "sty", "09", "Zdjęcia"),
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
            Expected("2024", "lut", "29", "Zdjęcia"),
            DestinationLayout.SubfolderFor(new DateTime(2024, 2, 29), MediaKind.Photo));
    }

    [Theory]
    [InlineData(2025, 12, 31, "2025", "gru", "31")]
    [InlineData(2026, 1, 1, "2026", "sty", "01")]
    public void SubfolderFor_HandlesYearBoundaries(
        int year, int month, int day, string expectedYear, string expectedMonth, string expectedDay)
    {
        Assert.Equal(
            Expected(expectedYear, expectedMonth, expectedDay, "Wideo"),
            DestinationLayout.SubfolderFor(new DateTime(year, month, day), MediaKind.Video));
    }

    [Fact]
    public void SubfolderFor_SeparatesWithThePlatformDirectoryCharacter()
    {
        string subfolder = DestinationLayout.SubfolderFor(new DateTime(2026, 3, 1), MediaKind.Photo);

        Assert.Equal(["2026", "mar", "01", "Zdjęcia"], subfolder.Split(Path.DirectorySeparatorChar));
    }

    // ----------------------------------------------------------------- no date

    [Fact]
    public void SubfolderFor_RoutesUndatedMediaToBezDaty()
    {
        Assert.Equal(Expected("Bez daty", "Zdjęcia"), DestinationLayout.SubfolderFor(null, MediaKind.Photo));
        Assert.Equal(Expected("Bez daty", "Wideo"), DestinationLayout.SubfolderFor(null, MediaKind.Video));
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
                Expected("2026", "mar", "01", "Zdjęcia"),
                DestinationLayout.SubfolderFor(new DateTime(2026, 3, 1), MediaKind.Photo));
        }
        finally
        {
            CultureInfo.CurrentCulture = original;
        }
    }
}
