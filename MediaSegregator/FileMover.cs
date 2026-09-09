namespace MediaSegregator;

/// <summary>Tally of one move run.</summary>
public sealed record MoveResult(int Moved, int Skipped, IReadOnlyList<string> Errors);

public static class FileMover
{
    /// <summary>
    /// Moves <paramref name="files"/> into <paramref name="destination"/>. A file already
    /// sitting in the destination is skipped rather than moved onto itself, and a name
    /// clash gets a " (n)" suffix so nothing is ever overwritten.
    /// </summary>
    public static MoveResult Move(
        IEnumerable<ScannedFile> files,
        string destination,
        CancellationToken token = default)
    {
        Directory.CreateDirectory(destination);
        string destinationFull = Path.TrimEndingDirectorySeparator(Path.GetFullPath(destination));

        int moved = 0;
        int skipped = 0;
        List<string> errors = [];

        foreach (ScannedFile file in files)
        {
            token.ThrowIfCancellationRequested();

            try
            {
                string source = Path.GetFullPath(file.Path);

                if (string.Equals(Path.GetDirectoryName(source), destinationFull, PathComparison))
                {
                    skipped++;
                    continue;
                }

                File.Move(source, UniqueTargetPath(destinationFull, file.Name));
                moved++;
            }
            catch (Exception ex)
            {
                errors.Add($"{file.Name}: {ex.Message}");
            }
        }

        return new MoveResult(moved, skipped, errors);
    }

    /// <summary>Appends " (1)", " (2)", … until the name is free in <paramref name="folder"/>.</summary>
    private static string UniqueTargetPath(string folder, string fileName)
    {
        string candidate = Path.Combine(folder, fileName);

        if (!File.Exists(candidate) && !Directory.Exists(candidate))
        {
            return candidate;
        }

        string stem = Path.GetFileNameWithoutExtension(fileName);
        string extension = Path.GetExtension(fileName);

        for (int i = 1; ; i++)
        {
            candidate = Path.Combine(folder, $"{stem} ({i}){extension}");

            if (!File.Exists(candidate) && !Directory.Exists(candidate))
            {
                return candidate;
            }
        }
    }

    private static StringComparison PathComparison =>
        OperatingSystem.IsWindows() ? StringComparison.OrdinalIgnoreCase : StringComparison.Ordinal;
}
