using System.Globalization;

namespace MediaSegregator.Tests;

/// <summary>
/// The nearest-place lookup, tested against the real list embedded in the app — there is no fixture
/// to build and no disk to touch, and using anything else would test a stand-in rather than what
/// ships. The coordinates below are the places' own, from the same GeoNames export.
///
/// Not covered: a fix on the far side of the antimeridian from its nearest place. The wrap is in
/// the code because coordinates do wrap, but the shipped world list has no close enough row to pin
/// it down without a stand-in fixture.
/// </summary>
public sealed class PlacesTests
{
    [Theory]
    [InlineData(52.2298, 21.0118, "Warszawa")]
    [InlineData(50.0614, 19.9366, "Kraków")]
    [InlineData(54.3523, 18.6491, "Gdańsk")]
    [InlineData(49.2990, 19.9489, "Zakopane")]
    public void NameFor_NamesThePlaceAFixSitsIn(double latitude, double longitude, string expected)
    {
        Assert.Equal(expected, Places.NameFor(new GeoPoint(latitude, longitude)));
    }

    /// <summary>
    /// The list runs down to hamlets, which is the point of it: a photo taken in the Bieszczady
    /// names the village it was taken by rather than the nearest town an hour's drive away.
    /// </summary>
    [Theory]
    [InlineData(49.1479, 22.4773, "Wetlina")]
    [InlineData(54.6081, 18.8008, "Hel")]
    public void NameFor_NamesVillagesToo(double latitude, double longitude, string expected)
    {
        Assert.Equal(expected, Places.NameFor(new GeoPoint(latitude, longitude)));
    }

    /// <summary>
    /// Photographs are taken beside a place rather than on its marker, so a fix a couple of
    /// kilometres out still lands in a folder named after somewhere real and nearby.
    /// </summary>
    [Fact]
    public void NameFor_NamesThePlaceAFixIsNear()
    {
        // Two kilometres north of Zakopane's marker, which the hamlet of Bystre is nearer to.
        Assert.Equal("Bystre", Places.NameFor(new GeoPoint(49.2750, 19.9700)));
    }

    /// <summary>
    /// GeoNames' Polish alternate names are not all names. These two rows arrived carrying a
    /// Wikipedia URL and a holiday-let advertisement, which had replaced the villages' own names
    /// and would have become folders called "https___en.wikipedia.org_wiki_Motarzyn" and
    /// "Dom 957 m2 nad jeziorem Iławskim…". Both are fixed in the data; the recipe in README.md
    /// now rejects such alternates so a regeneration cannot bring them back.
    /// </summary>
    [Theory]
    [InlineData(53.8753, 16.2343, "Motarzyn")]
    [InlineData(53.6003, 19.6130, "Nowa Wieś")]
    public void NameFor_RowsWhoseAlternateNameWasNotAName(double latitude, double longitude, string expected)
    {
        Assert.Equal(expected, Places.NameFor(new GeoPoint(latitude, longitude)));
    }

    // --------------------------------------------------------------- worldwide

    /// <summary>
    /// The world list means holiday photos land under real city names rather than coordinate
    /// labels, while the nearest-neighbour lookup still chooses by distance rather than by name.
    /// </summary>
    [Theory]
    [InlineData(52.5200, 13.4050, "Berlin")]
    [InlineData(50.0755, 14.4378, "Prague")]
    [InlineData(45.4408, 12.3155, "Venice")]
    [InlineData(-33.8688, 151.2093, "Sydney")]
    public void NameFor_AFixAbroad_NamesTheGlobalPlace(double latitude, double longitude, string expected)
    {
        Assert.Equal(expected, Places.NameFor(new GeoPoint(latitude, longitude)));
    }

    [Fact]
    public void NameFor_FarFromAnyPlace_FallsBackToCoordinates()
    {
        // The South Atlantic, with the whole ocean between it and the nearest row in the list.
        Assert.Equal("40.0S 30.0W", Places.NameFor(new GeoPoint(-40.0, -30.0)));
    }

    [Fact]
    public void NameFor_KeepsAsciiDigits_UnderALocaleThatDoesNot()
    {
        CultureInfo original = CultureInfo.CurrentCulture;

        try
        {
            // ar-EG renders numbers with Arabic-Indic digits when the current culture is used.
            CultureInfo.CurrentCulture = new CultureInfo("ar-EG");

            Assert.Equal("40.0S 30.0W", Places.NameFor(new GeoPoint(-40.0, -30.0)));
        }
        finally
        {
            CultureInfo.CurrentCulture = original;
        }
    }

    /// <summary>
    /// Some places in the list are spelled with a slash or a question mark, and whatever comes back
    /// here is used verbatim as one folder name.
    /// </summary>
    [Fact]
    public void NameFor_AlwaysReturnsAUsableFolderName()
    {
        char[] invalid = Path.GetInvalidFileNameChars();

        foreach (GeoPoint point in Fixes())
        {
            string name = Places.NameFor(point);

            Assert.NotEqual(string.Empty, name);
            Assert.Equal(-1, name.IndexOfAny(invalid));
            Assert.Equal(name, name.TrimEnd('.', ' '));
        }

        // A coarse world sweep, so the assertions meet real names and coordinate labels rather
        // than one chosen point.
        static IEnumerable<GeoPoint> Fixes()
        {
            for (double latitude = -80; latitude <= 80; latitude += 5)
            {
                for (double longitude = -180; longitude <= 180; longitude += 5)
                {
                    yield return new GeoPoint(latitude, longitude);
                }
            }
        }
    }
}
