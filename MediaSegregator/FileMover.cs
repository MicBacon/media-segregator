namespace MediaSegregator;

/// <summary>Tally of one move run.</summary>
public sealed record MoveResult(int Moved, int Skipped, IReadOnlyList<string> Errors, int Undated = 0);

public static class FileMover
{
    /// <summary>
    /// Moves <paramref name="files"/> into <paramref name="destination"/>, optionally routed into
    /// a subfolder chosen per file by <paramref name="targetFor"/> — pass
    /// <see cref="DestinationLayout.TargetFor"/> for the dated tree, or leave it null to land
    /// everything in the destination root. Subfolders are created on demand and reused when they
    /// already exist. A file already sitting in its target folder is skipped rather than moved
    /// onto itself, and a name clash gets a " (n)" suffix so nothing is ever overwritten.
    /// </summary>
    public static MoveResult Move(
        IEnumerable<ScannedFile> files,
        string destination,
        Func<ScannedFile, MoveTarget>? targetFor = null,
        CancellationToken token = default)
    {
        Directory.CreateDirectory(destination);
        string destinationFull = Path.TrimEndingDirectorySeparator(Path.GetFullPath(destination));

        int moved = 0;
        int skipped = 0;
        int undated = 0;
        List<string> errors = [];

        // One CreateDirectory per folder rather than per file: a run over a few thousand photos
        // otherwise repeats the same handful of syscalls for every single one.
        HashSet<string> created = new(PathComparer);

        foreach (ScannedFile file in files)
        {
            token.ThrowIfCancellationRequested();

            try
            {
                string source = Path.GetFullPath(file.Path);
                string folder = destinationFull;
                bool dated = true;

                if (targetFor is not null)
                {
                    MoveTarget target = targetFor(file);
                    dated = target.Dated;

                    folder = Path.TrimEndingDirectorySeparator(
                        Path.GetFullPath(Path.Combine(destinationFull, target.Subfolder)));

                    if (created.Add(folder))
                    {
                        Directory.CreateDirectory(folder);
                    }
                }

                if (string.Equals(Path.GetDirectoryName(source), folder, PathComparison))
                {
                    skipped++;
                    continue;
                }

                File.Move(source, UniqueTargetPath(folder, file.Name));
                moved++;

                // Counted only for files that actually moved, so the tally never claims more
                // undated media than the run reports having moved.
                if (!dated)
                {
                    undated++;
                }
            }
            catch (Exception ex)
            {
                errors.Add($"{file.Name}: {ex.Message}");
            }
        }

        return new MoveResult(moved, skipped, errors, undated);
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

    private static StringComparer PathComparer =>
        OperatingSystem.IsWindows() ? StringComparer.OrdinalIgnoreCase : StringComparer.Ordinal;
}
