using System.Globalization;
using MetadataDirectory = MetadataExtractor.Directory;

namespace MediaSegregator;

/// <summary>Where one file is headed, relative to the destination root.</summary>
public readonly record struct CopyTarget(string Subfolder, bool Dated);

/// <summary>
/// The folder tree the destination is organised into: "2026-03-01 Warszawa/Zdjęcia" for media that
/// knows when and where it was taken, "2026-03-01/Zdjęcia" when there is no location, and
/// "Bez daty/Wideo" when there is no date either. The day and the place share one folder name, so a
/// day spent in two towns becomes two folders side by side, sorted by date. <see cref="SubfolderFor"/>
/// is pure string work — the caller does the I/O — so the layout is testable without a disk.
/// </summary>
public static class DestinationLayout
{
    public const string UndatedFolder = "Bez daty";
    public const string PhotoFolder = "Zdjęcia";
    public const string VideoFolder = "Wideo";

    /// <summary>
    /// Joins the date and the place in one folder name: "2026-03-01 Warszawa". A space rather than
    /// an underscore, because nothing downstream has trouble with it — every place name has already
    /// been made folder-safe by <see cref="Places"/>, and the copier only ever handles the result as
    /// a whole path segment, never splits it.
    /// </summary>
    public const char PlaceSeparator = ' ';

    /// <summary>
    /// The subfolder <paramref name="kind"/> media taken on <paramref name="taken"/> at
    /// <paramref name="place"/> belongs in, relative to the destination root: the date and the place
    /// joined into one folder, with the media kind below it. <see cref="UndatedFolder"/> stands in
    /// for an unknown date, and the place is left off when it is unknown.
    /// Built with Path.Combine rather than literal separators so the result compares equal to a
    /// directory name read back off disk on Windows, and formatted invariantly so a locale with
    /// non-ASCII digits cannot leak into a folder name.
    /// </summary>
    public static string SubfolderFor(DateTime? taken, MediaKind kind, string? place = null)
    {
        string dateName = taken is { } date
            ? date.ToString("yyyy-MM-dd", CultureInfo.InvariantCulture)
            : UndatedFolder;

        string dayFolder = string.IsNullOrWhiteSpace(place)
            ? dateName
            : $"{dateName}{PlaceSeparator}{place.Trim()}";

        string kindFolder = kind == MediaKind.Photo ? PhotoFolder : VideoFolder;

        return Path.Combine(dayFolder, kindFolder);
    }

    /// <summary>
    /// Reads the capture date and the coordinates off disk and routes accordingly. This is where
    /// the pure halves meet, and the delegate <see cref="FileCopier.Copy"/> is handed by the app.
    /// </summary>
    public static CopyTarget TargetFor(ScannedFile file)
    {
        // Read once and asked twice: the date and the coordinates live in the same directories, and
        // a run of a hundred thousand photos should not open and parse every one of them twice.
        IReadOnlyList<MetadataDirectory> metadata = Metadata.Read(file.Path);

        DateTime? taken = MediaDate.Taken(file, metadata);
        string? place = MediaLocation.Of(metadata) is { } point ? Places.NameFor(point) : null;

        return new CopyTarget(SubfolderFor(taken, file.Kind, place), taken is not null);
    }
}
