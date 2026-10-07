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
    [InlineData(52.4069, 16.9299, "Poznań")]
    [InlineData(53.4289, 14.5530, "Szczecin")]
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
    /// Not every GeoNames name is a name. These two records carry a Wikipedia URL and a
    /// holiday-let advertisement in their <em>primary</em> name column, and neither has a Polish
    /// alternate to recover from, so without the correction they would ship as folders called
    /// "https___en.wikipedia.org_wiki_Motarzyn" and "Dom 957 m2 nad jeziorem Iławskim…". The
    /// recipe in README.md fixes both by GeoNames id; this test is what fails if a regeneration
    /// of the data file loses that correction.
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

    // ------------------------------------------------------ districts and cities

    /// <summary>
    /// A city keeps its name across the whole of itself. GeoNames holds a city's districts as
    /// populated places in their own right, each nearer to the camera than the city marker is, so
    /// a photograph of the Opéra was filed under "Paris 09 Opéra", one from Gdańsk's Przymorze
    /// under "Przymorze Małe" and one from Ursynów under "Ursynów". Those rows are dropped when
    /// the list is built; these are their own coordinates, the worst case, standing exactly where
    /// the dropped record used to win.
    /// </summary>
    [Theory]
    [InlineData(48.8718, 2.3399, "Paris")]        // was Paris 09 Opéra
    [InlineData(48.8925, 2.3444, "Paris")]        // was Paris 18 Buttes-Montmartre
    [InlineData(43.2829, 5.3602, "Marseille")]    // was Marseille 07
    [InlineData(54.4098, 18.5784, "Gdańsk")]      // was Przymorze Małe
    [InlineData(54.4072, 18.5536, "Gdańsk")]      // was Oliwa
    [InlineData(54.3944, 18.6023, "Gdańsk")]      // was Zaspa
    [InlineData(52.1505, 21.0504, "Warszawa")]    // was Ursynów
    [InlineData(52.2924, 20.9353, "Warszawa")]    // was Bielany
    [InlineData(50.0617, 19.9373, "Kraków")]      // Rynek Główny, was Stare Miasto
    [InlineData(51.1100, 17.0313, "Wrocław")]     // Rynek, was Stare Miasto
    [InlineData(51.7592, 19.4560, "Łódź")]        // Piotrkowska, was Stare Polesie
    [InlineData(50.2664, 19.0238, "Katowice")]    // Spodek, was Zespół dzielnic śródmiejskich
    [InlineData(51.5014, 7.4108, "Dortmund")]     // was Dorstfeld
    public void NameFor_PrefersTheCity_OverItsOwnDistricts(
        double latitude, double longitude, string expected)
    {
        Assert.Equal(expected, Places.NameFor(new GeoPoint(latitude, longitude)));
    }

    /// <summary>
    /// The other side of dropping a city's districts, and the reason it is keyed on administration
    /// rather than on distance: these are all nearer to a big city's marker than some of its own
    /// districts are, and every one of them is a town in its own right. Sopot is closer to
    /// Gdańsk's marker than Przymorze Małe was.
    /// </summary>
    [Theory]
    [InlineData(54.4418, 18.5600, "Sopot")]       // nearer Gdańsk's marker than Przymorze was
    [InlineData(54.5189, 18.5319, "Gdynia")]
    [InlineData(48.8486, 2.4377, "Vincennes")]    // just outside the Paris boundary
    [InlineData(48.8642, 2.4432, "Montreuil")]
    [InlineData(48.9070, 2.3330, "Saint-Ouen")]
    public void NameFor_KeepsATownBesideTheCity(double latitude, double longitude, string expected)
    {
        Assert.Equal(expected, Places.NameFor(new GeoPoint(latitude, longitude)));
    }

    /// <summary>
    /// The threshold that protects villages a municipal merger filed under a town's code. Nevele
    /// is eleven thousand people six kilometres from Deinze and shares its commune code; Templeuve
    /// shares Tournai's. Both towns fall short of the population at which districts are dropped,
    /// which is the only thing keeping these two names in the list.
    /// </summary>
    [Theory]
    [InlineData(51.0353, 3.5457, "Nevele")]
    [InlineData(50.6441, 3.2801, "Templeuve")]
    public void NameFor_KeepsAVillageTheTownAbsorbed(double latitude, double longitude, string expected)
    {
        Assert.Equal(expected, Places.NameFor(new GeoPoint(latitude, longitude)));
    }

    /// <summary>
    /// The other half of the same rule, and the one that costs the most if it is got wrong: a
    /// hamlet has no city beside it to lose to, so it still names the folder. Population breaks a
    /// tie between records standing on each other — it is not a filter, and it must never become
    /// one: 88.5% of the Polish rows, these three included, carry a population of nought.
    /// </summary>
    [Theory]
    [InlineData(49.1479, 22.4773, "Wetlina")]     // a Bieszczady village
    [InlineData(49.2511, 19.9336, "Bystre")]      // the summit of Giewont, named for the hamlet below
    [InlineData(53.7667, 21.7333, "Suchy Róg")]   // out on Lake Śniardwy
    public void NameFor_StillNamesTheHamlet_WhenNothingBiggerIsBeside(
        double latitude, double longitude, string expected)
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
