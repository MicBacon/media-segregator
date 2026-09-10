using System.Globalization;
using System.Text.RegularExpressions;
using MetadataExtractor;
using MetadataExtractor.Formats.Exif;
using MetadataExtractor.Formats.QuickTime;
using MetadataDirectory = MetadataExtractor.Directory;

namespace MediaSegregator;

/// <summary>
/// The date a photo or video was taken, read from the file's own metadata and — failing that —
/// from the date camera apps bake into the file name.
///
/// The file system's timestamp is deliberately not consulted: on media copied off a phone it is
/// the date of the copy, not of the shot, so trusting it would file a whole library under one
/// wrong day. Media with no trustworthy date is reported as such and lands in "Bez daty".
/// </summary>
public static partial class MediaDate
{
    /// <summary>A camera with a dead clock emits 1970 or 1980; nothing here predates digital.</summary>
    private static readonly DateTime Earliest = new(1990, 1, 1);

    /// <summary>The capture date, or null when the file admits to none.</summary>
    public static DateTime? Taken(ScannedFile file) =>
        FromMetadata(file.Path) ?? FromFileName(file.Name);

    private static DateTime? FromMetadata(string path)
    {
        IReadOnlyList<MetadataDirectory> directories;

        try
        {
            directories = ImageMetadataReader.ReadMetadata(path);
        }
        catch (Exception)
        {
            // Unrecognised container, truncated file, no read access — the name may still know.
            return null;
        }

        return AppleCreationDate(directories)
            ?? Probe<ExifSubIfdDirectory>(directories, ExifSubIfdDirectory.TagDateTimeOriginal)
            ?? Probe<ExifSubIfdDirectory>(directories, ExifSubIfdDirectory.TagDateTimeDigitized)
            ?? Probe<ExifIfd0Directory>(directories, ExifIfd0Directory.TagDateTime)
            // mvhd.creation_time is UTC per the ISO base media spec, but Android camera apps
            // overwhelmingly write local time there, and the formats this app scans are the
            // Android ones. Taken as-is, therefore, with no UTC conversion: the iPhone case is
            // already handled above by the offset-bearing tag, which is checked first.
            ?? Probe<QuickTimeMovieHeaderDirectory>(directories, QuickTimeMovieHeaderDirectory.TagCreated);
    }

    private static DateTime? Probe<T>(IReadOnlyList<MetadataDirectory> directories, int tag)
        where T : MetadataDirectory
    {
        foreach (T directory in directories.OfType<T>())
        {
            if (directory.TryGetDateTime(tag, out DateTime value) && Plausible(value))
            {
                return value;
            }
        }

        return null;
    }

    /// <summary>
    /// com.apple.quicktime.creationdate, which unlike the movie header carries the device's own
    /// UTC offset. The wall-clock half of it is the date the owner remembers taking the video.
    /// </summary>
    private static DateTime? AppleCreationDate(IReadOnlyList<MetadataDirectory> directories)
    {
        foreach (QuickTimeMetadataHeaderDirectory directory in
                 directories.OfType<QuickTimeMetadataHeaderDirectory>())
        {
            if (TryParseOffsetDate(directory.GetString(QuickTimeMetadataHeaderDirectory.TagCreationDate),
                    out DateTime value)
                && Plausible(value))
            {
                return value;
            }
        }

        return null;
    }

    private static bool TryParseOffsetDate(string? raw, out DateTime value)
    {
        value = default;

        if (string.IsNullOrWhiteSpace(raw))
        {
            return false;
        }

        if (!DateTimeOffset.TryParse(raw, CultureInfo.InvariantCulture, DateTimeStyles.None,
                out DateTimeOffset parsed)
            // Some writers use the ISO 8601 basic offset (+0100) where .NET wants +01:00.
            && !DateTimeOffset.TryParse(CompactOffset().Replace(raw, "$1:$2"), CultureInfo.InvariantCulture,
                DateTimeStyles.None, out parsed))
        {
            return false;
        }

        value = parsed.DateTime;

        return true;
    }

    /// <summary>
    /// The date a camera app wrote into the file name — IMG_20260301_142233.jpg,
    /// IMG-20260301-WA0001.jpg, Signal-2026-03-01-142233.jpg and their relatives. The digit
    /// boundaries are what stop an epoch-milliseconds name being misread as a date.
    /// </summary>
    public static DateTime? FromFileName(string name)
    {
        string stem = Path.GetFileNameWithoutExtension(name);

        foreach (Regex pattern in Patterns)
        {
            foreach (Match match in pattern.Matches(stem))
            {
                if (TryDate(match, out DateTime value))
                {
                    return value;
                }
            }
        }

        return null;
    }

    private static bool TryDate(Match match, out DateTime value)
    {
        value = default;

        int year = int.Parse(match.Groups["y"].ValueSpan, CultureInfo.InvariantCulture);
        int month = int.Parse(match.Groups["m"].ValueSpan, CultureInfo.InvariantCulture);
        int day = int.Parse(match.Groups["d"].ValueSpan, CultureInfo.InvariantCulture);

        if (month is < 1 or > 12 || day < 1 || day > DateTime.DaysInMonth(year, month))
        {
            return false;
        }

        value = new DateTime(year, month, day);

        return Plausible(value);
    }

    private static bool Plausible(DateTime value) =>
        value >= Earliest && value.Date <= DateTime.Today.AddDays(1);

    // Most specific first: the 14-digit form must be tried before the 8-digit one, which its
    // trailing digit boundary would otherwise reject outright.
    private static Regex[] Patterns => [SeparatedDate(), CompactDateTime(), CompactDate()];

    /// <summary>
    /// 2026-03-01, 2026_03_01, 2026.03.01 — and the unpadded spellings, because macOS names
    /// screenshots "Zrzut ekranu 2024-06-5 o 17.09.31.png" with a single-digit day.
    /// </summary>
    [GeneratedRegex(@"(?<!\d)(?<y>(?:19|20)\d{2})[-_.](?<m>\d{1,2})[-_.](?<d>\d{1,2})(?!\d)",
        RegexOptions.CultureInvariant)]
    private static partial Regex SeparatedDate();

    /// <summary>20260301142233, as in ...BURST20260301142233_COVER.jpg</summary>
    [GeneratedRegex(@"(?<!\d)(?<y>(?:19|20)\d{2})(?<m>\d{2})(?<d>\d{2})\d{6}(?!\d)",
        RegexOptions.CultureInvariant)]
    private static partial Regex CompactDateTime();

    /// <summary>20260301, as in IMG_20260301_142233 and IMG-20260301-WA0001</summary>
    [GeneratedRegex(@"(?<!\d)(?<y>(?:19|20)\d{2})(?<m>\d{2})(?<d>\d{2})(?!\d)",
        RegexOptions.CultureInvariant)]
    private static partial Regex CompactDate();

    /// <summary>A trailing ±HHMM offset, captured as ±HH and MM.</summary>
    [GeneratedRegex(@"([+-]\d{2})(\d{2})$", RegexOptions.CultureInvariant)]
    private static partial Regex CompactOffset();
}
