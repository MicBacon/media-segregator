using System.Buffers;
using System.Globalization;
using System.Text;

namespace MediaSegregator;

/// <summary>
/// The folder name a set of coordinates deserves: the nearest place from a list shipped inside the
/// executable, so the app needs no network and no API key to say "Zakopane" instead of a number.
///
/// The list covers the world from GeoNames cities500, with the full Polish populated-place list
/// kept alongside it so domestic photos still resolve down to villages and hamlets. It is trimmed
/// to name, latitude and longitude and embedded as <c>Data/cities.tsv</c>. Names read as GeoNames
/// spells them, so abroad they are the local or English form — "Praha" comes out "Prague" — while
/// Polish places carry their Polish names. Nothing here throws: a missing or damaged resource
/// simply means every shot is filed under its coordinates.
///
/// GeoNames holds a city's districts as populated places in their own right — Przymorze Małe
/// beside Gdańsk, "Paris 09 Opéra" beside Paris — every one of them nearer to a camera in the city
/// than the city marker is, so plain nearest-wins named folders after districts. Two things fix
/// that, and both come from the data rather than from a guess. The districts are dropped when the
/// list is built, by administrative code; and the distance each of them stood from its city is
/// kept as that city's <em>reach</em>, so a fix is credited to the place it lies deepest inside
/// rather than to the marker it happens to be nearest. The recipe in README.md builds both.
///
/// A village has no districts and so no measured reach, falling back to
/// <see cref="DefaultReachKm"/> — which is why a photograph in the Bieszczady is still filed under
/// the hamlet it was taken in rather than under a town an hour away.
/// </summary>
public static class Places
{
    /// <summary>
    /// Past this, the nearest place says nothing true about where the shot was taken: open sea,
    /// desert and ice get coordinates rather than the name of whatever lies over the horizon.
    /// Fifteen kilometres is generous anywhere inhabited, the list being dense enough to put a
    /// settlement within a few kilometres, and short enough that a fix in the middle of the Sahara
    /// does not borrow a name from several hundred away.
    /// </summary>
    private const double MaxDistanceKm = 15;

    /// <summary>
    /// How far up and down the latitude-sorted list a query has to look. Derived from the threshold
    /// rather than written as a number, because the two must not drift apart: a degree of latitude
    /// is 110.574 km at its shortest, so dividing by a deliberately low 110 leaves the band a
    /// margin over <see cref="MaxDistanceKm"/> and no place within range can fall outside it.
    /// Keeping it tight is what keeps a query cheap — the world list holds a quarter of a million
    /// rows, and a band four times wider than necessary measures four times as many of them.
    /// </summary>
    private const double BandDegrees = MaxDistanceKm / 110.0;

    /// <summary>
    /// The reach credited to a place the data gives no extent for, which is every village and
    /// hamlet. Two kilometres is wide enough that a photograph taken at the edge of a village is
    /// still filed under it, and narrow enough that the village next door is not.
    /// </summary>
    private const double DefaultReachKm = 2;


    private const string ResourceName = "MediaSegregator.Data.cities.tsv";

    private static readonly Lazy<CityIndex> Cities = new(Load);

    /// <summary>
    /// The nearest place to <paramref name="point"/>, or a coordinate label such as "52.2N 21.0E"
    /// when nothing is close enough. Always a name usable as a single folder.
    /// </summary>
    public static string NameFor(GeoPoint point) => Nearest(point) ?? CoordinateLabel(point);

    private static string? Nearest(GeoPoint point)
    {
        CityIndex cities = Cities.Value;

        if (cities.Latitudes.Length == 0)
        {
            return null;
        }

        // The list is sorted by latitude, so only a band around the point has to be measured.
        int index = Array.BinarySearch(cities.Latitudes, (float)(point.Latitude - BandDegrees));

        if (index < 0)
        {
            index = ~index;
        }

        double limit = point.Latitude + BandDegrees;
        // One cosine for the whole query rather than one per candidate: over this few kilometres the
        // flat-earth approximation is accurate to metres, far below the resolution of the answer.
        double longitudeScale = Math.Cos(point.Latitude * Math.PI / 180) * 111.320;

        // Not the shortest distance but the deepest containment: each candidate's distance measured
        // against how far that place itself extends. Gdańsk's marker is twice as far from Przymorze
        // as Sopot's is, and Przymorze is nevertheless in Gdańsk and not in Sopot; dividing by the
        // reach is what says so. For the villages, where every reach is the same default, the
        // ranking collapses back to plain distance.
        double bestScore = double.MaxValue;
        string? best = null;

        for (int i = index; i < cities.Latitudes.Length && cities.Latitudes[i] <= limit; i++)
        {
            double northSouth = (cities.Latitudes[i] - point.Latitude) * 110.574;
            double eastWest = WrappedDegrees(cities.Longitudes[i] - point.Longitude) * longitudeScale;
            double km = Math.Sqrt((northSouth * northSouth) + (eastWest * eastWest));

            if (km > MaxDistanceKm)
            {
                continue;
            }

            double score = km / Math.Max(DefaultReachKm, cities.Reaches[i]);

            if (score < bestScore)
            {
                bestScore = score;
                best = cities.Names[i];
            }
        }

        return best;
    }

    /// <summary>
    /// Brings a longitude difference back into ±180, so a point just east of the antimeridian is
    /// eleven kilometres from one just west of it rather than most of the way round the globe.
    /// </summary>
    private static double WrappedDegrees(double difference) => difference switch
    {
        > 180 => difference - 360,
        < -180 => difference + 360,
        _ => difference,
    };

    /// <summary>"52.2N 21.0E" — one decimal is about 11 km, which is the grain of a place name.</summary>
    private static string CoordinateLabel(GeoPoint point) =>
        string.Create(CultureInfo.InvariantCulture,
            $"{Math.Abs(point.Latitude):0.0}{(point.Latitude >= 0 ? 'N' : 'S')} "
            + $"{Math.Abs(point.Longitude):0.0}{(point.Longitude >= 0 ? 'E' : 'W')}");

    private static CityIndex Load()
    {
        try
        {
            using Stream? stream = typeof(Places).Assembly.GetManifestResourceStream(ResourceName);

            if (stream is null)
            {
                return CityIndex.Empty;
            }

            using StreamReader reader = new(stream, Encoding.UTF8);
            List<City> cities = [];

            while (reader.ReadLine() is { } line)
            {
                if (TryParse(line, out City city))
                {
                    cities.Add(city);
                }
            }

            // Sorted here rather than trusted from the file, because the binary search above is
            // what keeps a lookup cheap and a reordered data file must not quietly break it.
            cities.Sort(static (left, right) => left.Latitude.CompareTo(right.Latitude));

            return new CityIndex(
                [.. cities.Select(static city => city.Name)],
                [.. cities.Select(static city => city.Latitude)],
                [.. cities.Select(static city => city.Longitude)],
                [.. cities.Select(static city => city.Reach)]);
        }
        catch (Exception)
        {
            return CityIndex.Empty;
        }
    }

    /// <summary>
    /// One "name\tlatitude\tlongitude\treach" row, skipped rather than fatal when malformed. The
    /// reach is how far the place extends in kilometres, measured when the list is built from the
    /// districts the place was collapsed from; it is optional and absent reads as nought, which
    /// simply leaves the row on <see cref="DefaultReachKm"/> like any village.
    /// </summary>
    private static bool TryParse(string line, out City city)
    {
        city = default;

        ReadOnlySpan<char> row = line;
        int afterName = row.IndexOf('\t');

        // The upper bound keeps a damaged row from stack-allocating its way through the sanitiser,
        // and no settlement worth a folder is named in two hundred characters.
        if (afterName is <= 0 or > 200)
        {
            return false;
        }

        ReadOnlySpan<char> rest = row[(afterName + 1)..];
        int afterLatitude = rest.IndexOf('\t');

        if (afterLatitude <= 0
            || !float.TryParse(rest[..afterLatitude], NumberStyles.Float, CultureInfo.InvariantCulture,
                out float latitude))
        {
            return false;
        }

        rest = rest[(afterLatitude + 1)..];
        int afterLongitude = rest.IndexOf('\t');
        ReadOnlySpan<char> longitudeText = afterLongitude < 0 ? rest : rest[..afterLongitude];

        if (!float.TryParse(longitudeText, NumberStyles.Float, CultureInfo.InvariantCulture,
                out float longitude))
        {
            return false;
        }

        if (afterLongitude < 0
            || !double.TryParse(rest[(afterLongitude + 1)..], NumberStyles.Float,
                CultureInfo.InvariantCulture, out double reach)
            || reach < 0)
        {
            reach = 0;
        }

        string name = Sanitised(row[..afterName]);

        if (name.Length == 0)
        {
            return false;
        }

        city = new City(latitude, longitude, name, reach);

        return true;
    }

    /// <summary>
    /// Makes a place name safe as a folder name. A handful of towns are spelled with a slash or a
    /// question mark, and Windows also refuses a name ending in a dot or a space.
    /// </summary>
    private static string Sanitised(ReadOnlySpan<char> name)
    {
        Span<char> buffer = stackalloc char[name.Length];
        name.CopyTo(buffer);

        int at;

        while ((at = buffer.IndexOfAny(InvalidNameChars)) >= 0)
        {
            buffer[at] = '_';
        }

        return buffer.Trim().TrimEnd('.').TrimEnd().ToString();
    }

    /// <summary>
    /// Resolved once, and on the running platform: macOS forbids only the separator, Windows a
    /// whole table, and the folder has to be legal wherever the app happens to be sorting.
    /// </summary>
    private static readonly SearchValues<char> InvalidNameChars =
        SearchValues.Create(Path.GetInvalidFileNameChars());

    /// <summary>One row of the list, as it is read before being split into the arrays below.</summary>
    private readonly record struct City(float Latitude, float Longitude, string Name, double Reach);

    /// <summary>Four parallel arrays ordered by latitude; the index into one indexes them all.</summary>
    private sealed record CityIndex(
        string[] Names, float[] Latitudes, float[] Longitudes, double[] Reaches)
    {
        public static CityIndex Empty { get; } = new([], [], [], []);
    }
}
