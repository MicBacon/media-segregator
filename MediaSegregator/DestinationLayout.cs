using System.Globalization;

namespace MediaSegregator;

/// <summary>Where one file is headed, relative to the destination root.</summary>
public readonly record struct MoveTarget(string Subfolder, bool Dated);

/// <summary>
/// The folder tree the destination is organised into: "2026/mar/01/Zdjęcia" for media whose
/// capture date is known, "Bez daty/Wideo" for the rest. <see cref="SubfolderFor"/> is pure
/// string work — the caller does the I/O — so the layout is testable without a disk.
/// </summary>
public static class DestinationLayout
{
    public const string UndatedFolder = "Bez daty";
    public const string PhotoFolder = "Zdjęcia";
    public const string VideoFolder = "Wideo";

    private static readonly string[] Months =
        ["sty", "lut", "mar", "kwi", "maj", "cze", "lip", "sie", "wrz", "paź", "lis", "gru"];

    /// <summary>
    /// The subfolder <paramref name="kind"/> media taken on <paramref name="taken"/> belongs in,
    /// relative to the destination root; <see cref="UndatedFolder"/> when the date is unknown.
    /// Built with Path.Combine rather than literal separators so the result compares equal to a
    /// directory name read back off disk on Windows, and formatted invariantly so a locale with
    /// non-ASCII digits cannot leak into a folder name.
    /// </summary>
    public static string SubfolderFor(DateTime? taken, MediaKind kind)
    {
        string kindFolder = kind == MediaKind.Photo ? PhotoFolder : VideoFolder;

        return taken is { } date
            ? Path.Combine(
                date.Year.ToString("0000", CultureInfo.InvariantCulture),
                Months[date.Month - 1],
                date.Day.ToString("00", CultureInfo.InvariantCulture),
                kindFolder)
            : Path.Combine(UndatedFolder, kindFolder);
    }

    /// <summary>
    /// Reads the capture date off disk and routes accordingly. This is where the two halves meet,
    /// and the delegate <see cref="FileMover.Move"/> is handed by the app.
    /// </summary>
    public static MoveTarget TargetFor(ScannedFile file)
    {
        DateTime? taken = MediaDate.Taken(file);

        return new MoveTarget(SubfolderFor(taken, file.Kind), taken is not null);
    }
}
