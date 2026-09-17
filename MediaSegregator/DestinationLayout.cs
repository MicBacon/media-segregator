using System.Globalization;
using MetadataDirectory = MetadataExtractor.Directory;

namespace MediaSegregator;

/// <summary>Where one file is headed, relative to the destination root.</summary>
public readonly record struct CopyTarget(string Subfolder, bool Dated);

/// <summary>
/// The folder tree the destination is organised into: "2026_03_01/Zdjęcia/Warszawa" for media that
/// knows when and where it was taken, "2026_03_01/Zdjęcia" when there is no location, and
/// "Bez daty/Wideo" when there is no date either. <see cref="SubfolderFor"/> is pure string work —
/// the caller does the I/O — so the layout is testable without a disk.
/// </summary>
public static class DestinationLayout
{
    public const string UndatedFolder = "Bez daty";
    public const string PhotoFolder = "Zdjęcia";
    public const string VideoFolder = "Wideo";

    /// <summary>
    /// The subfolder <paramref name="kind"/> media taken on <paramref name="taken"/> at
    /// <paramref name="place"/> belongs in, relative to the destination root;
    /// <see cref="UndatedFolder"/> when the date is unknown and no third segment when the place is.
    /// Built with Path.Combine rather than literal separators so the result compares equal to a
    /// directory name read back off disk on Windows, and formatted invariantly so a locale with
    /// non-ASCII digits cannot leak into a folder name.
    /// </summary>
    public static string SubfolderFor(DateTime? taken, MediaKind kind, string? place = null)
    {
        string dateFolder = taken is { } date
            ? date.ToString("yyyy_MM_dd", CultureInfo.InvariantCulture)
            : UndatedFolder;

        string kindFolder = kind == MediaKind.Photo ? PhotoFolder : VideoFolder;

        return string.IsNullOrWhiteSpace(place)
            ? Path.Combine(dateFolder, kindFolder)
            : Path.Combine(dateFolder, kindFolder, place);
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
