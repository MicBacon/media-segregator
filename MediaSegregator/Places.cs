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
/// to name, latitude and longitude and embedded as <c>Data/cities.tsv</c>. Nothing here throws: a
/// missing or damaged resource simply means every shot is filed under its coordinates.
/// </summary>
public static class Places
{
    /// <summary>
    /// Past this, the nearest place says nothing true about where the shot was taken. Fifteen
    /// kilometres is generous inside Poland, where the list puts a village within a few kilometres
    /// of anywhere, while still keeping remote fixes away from misleading city names.
    /// </summary>
    private const double MaxDistanceKm = 15;

    /// <summary>Half a degree of latitude is 55 km, so the band cannot miss a place within range.</summary>
    private const double BandDegrees = 0.5;

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
        // One cosine for the whole query rather than one per candidate: over 50 km the flat-earth
        // approximation is accurate to a few metres, which is far below the resolution of the answer.
        double longitudeScale = Math.Cos(point.Latitude * Math.PI / 180) * 111.320;
        double bestKm = MaxDistanceKm;
        string? best = null;

        for (int i = index; i < cities.Latitudes.Length && cities.Latitudes[i] <= limit; i++)
        {
            double northSouth = (cities.Latitudes[i] - point.Latitude) * 110.574;
            double eastWest = WrappedDegrees(cities.Longitudes[i] - point.Longitude) * longitudeScale;
            double km = Math.Sqrt((northSouth * northSouth) + (eastWest * eastWest));

            if (km < bestKm)
            {
                bestKm = km;
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
            List<(float Latitude, float Longitude, string Name)> cities = [];

            while (reader.ReadLine() is { } line)
            {
                if (TryParse(line, out (float Latitude, float Longitude, string Name) city))
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
                [.. cities.Select(static city => city.Longitude)]);
        }
        catch (Exception)
        {
            return CityIndex.Empty;
        }
    }

    /// <summary>One "name\tlatitude\tlongitude" row, skipped rather than fatal when malformed.</summary>
    private static bool TryParse(string line, out (float Latitude, float Longitude, string Name) city)
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
                out float latitude)
            || !float.TryParse(rest[(afterLatitude + 1)..], NumberStyles.Float, CultureInfo.InvariantCulture,
                out float longitude))
        {
            return false;
        }

        string name = Sanitised(row[..afterName]);

        if (name.Length == 0)
        {
            return false;
        }

        city = (latitude, longitude, name);

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

    /// <summary>Three parallel arrays ordered by latitude; the index into one indexes them all.</summary>
    private sealed record CityIndex(string[] Names, float[] Latitudes, float[] Longitudes)
    {
        public static CityIndex Empty { get; } = new([], [], []);
    }
}
