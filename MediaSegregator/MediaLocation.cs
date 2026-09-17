using System.Globalization;
using System.Text;
using System.Text.RegularExpressions;
using MetadataExtractor;
using MetadataExtractor.Formats.Exif;
using MetadataExtractor.Formats.QuickTime;
using MetadataDirectory = MetadataExtractor.Directory;

namespace MediaSegregator;

/// <summary>Where a shot was taken, in signed decimal degrees.</summary>
public readonly record struct GeoPoint(double Latitude, double Longitude);

/// <summary>
/// The coordinates a camera wrote into a photo or video, read from the file's own metadata.
///
/// Stills carry a GPS IFD, which MetadataExtractor hands over already converted to degrees. Video
/// carries an ISO 6709 string instead — iPhones under the QuickTime metadata key
/// com.apple.quicktime.location.ISO6709, Android under the older <c>moov/udta/©xyz</c> atom — and
/// the reader surfaces both under the same tag, so one parser covers them.
/// </summary>
public static partial class MediaLocation
{
    /// <summary>The coordinates of <paramref name="file"/>, or null when it carries none.</summary>
    public static GeoPoint? Of(ScannedFile file) => Of(Metadata.Read(file.Path));

    /// <summary>
    /// The same answer from metadata already read, for the routing that also wants the capture
    /// date and should not open the file a second time to get it. An unreadable or unrecognised
    /// file arrives here as an empty list, which is nothing to place.
    /// </summary>
    public static GeoPoint? Of(IReadOnlyList<MetadataDirectory> directories) =>
        ExifGps(directories) ?? QuickTimeGps(directories);

    /// <summary>The GPS IFD of a still, which MetadataExtractor already assembles into degrees.</summary>
    private static GeoPoint? ExifGps(IReadOnlyList<MetadataDirectory> directories)
    {
        foreach (GpsDirectory directory in directories.OfType<GpsDirectory>())
        {
            if (directory.GetGeoLocation() is { } location
                && Plausible(location.Latitude, location.Longitude) is { } point)
            {
                return point;
            }
        }

        return null;
    }

    /// <summary>
    /// The ISO 6709 string a video carries: com.apple.quicktime.location.ISO6709 on an iPhone,
    /// the moov/udta/©xyz atom on Android. The reader maps both onto this one tag.
    /// </summary>
    private static GeoPoint? QuickTimeGps(IReadOnlyList<MetadataDirectory> directories)
    {
        foreach (QuickTimeMetadataHeaderDirectory directory in
                 directories.OfType<QuickTimeMetadataHeaderDirectory>())
        {
            if (FromIso6709(directory.GetString(QuickTimeMetadataHeaderDirectory.TagGpsLocation)) is { } point)
            {
                return point;
            }
        }

        return null;
    }

    /// <summary>
    /// An ISO 6709 point as a camera writes it: "+52.2297+021.0122/" from Android,
    /// "+52.2297+021.0122+100.000/" with altitude from an iPhone.
    ///
    /// Only the signed-decimal-degrees spelling is accepted. ISO 6709 also permits packed
    /// degrees-minutes ("+5213.7+02100.7/"), which is indistinguishable from decimal degrees
    /// without counting digits — and since no camera in this app's world writes it, guessing
    /// wrong would be worse than reporting no location at all.
    /// </summary>
    public static GeoPoint? FromIso6709(string? raw)
    {
        if (string.IsNullOrWhiteSpace(raw))
        {
            return null;
        }

        Match match = Iso6709Point().Match(raw.Trim());

        if (!match.Success
            || !double.TryParse(match.Groups["lat"].ValueSpan, NumberStyles.Float,
                CultureInfo.InvariantCulture, out double latitude)
            || !double.TryParse(match.Groups["lon"].ValueSpan, NumberStyles.Float,
                CultureInfo.InvariantCulture, out double longitude))
        {
            return null;
        }

        return Plausible(latitude, longitude);
    }

    /// <summary>
    /// Rejects what cannot be a fix. Exactly (0, 0) is the sea off Ghana, but in a camera it is
    /// what "location services never got a fix" looks like, so it is treated as no location.
    /// </summary>
    private static GeoPoint? Plausible(double latitude, double longitude) =>
        latitude is >= -90 and <= 90
        && longitude is >= -180 and <= 180
        && (latitude != 0 || longitude != 0)
            ? new GeoPoint(latitude, longitude)
            : null;

    /// <summary>
    /// Latitude then longitude, each signed, with an optional altitude and the closing solidus.
    /// The two-digit cap on the latitude's whole part is what rejects the packed spelling.
    /// </summary>
    [GeneratedRegex(@"^(?<lat>[+-]\d{1,2}(?:\.\d+)?)(?<lon>[+-]\d{1,3}(?:\.\d+)?)(?:[+-]\d+(?:\.\d+)?)?/?$",
        RegexOptions.CultureInvariant)]
    private static partial Regex Iso6709Point();
}
