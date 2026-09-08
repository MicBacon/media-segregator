using System.IO.Enumeration;

namespace MediaSegregator;

/// <summary>One file discovered by a scan.</summary>
public sealed record ScannedFile(string Path, string Name, long Length, DateTime Modified)
{
    public string SizeDisplay => Length switch
    {
        < 1024 => $"{Length} B",
        < 1024 * 1024 => $"{Length / 1024.0:0.#} KB",
        < 1024L * 1024 * 1024 => $"{Length / (1024.0 * 1024):0.#} MB",
        _ => $"{Length / (1024.0 * 1024 * 1024):0.##} GB",
    };
}

public static class FileScanner
{
    /// <summary>Folder the executable lives in, regardless of where it was launched from.</summary>
    public static string DefaultFolder => AppContext.BaseDirectory;

    /// <summary>
    /// Streams the files in <paramref name="folder"/>. FileSystemEnumerable reads name,
    /// size and timestamps straight out of the directory-enumeration buffer, so there is
    /// no second stat() per file and no FileInfo allocation; the full path string is only
    /// built for entries that pass the filter.
    /// </summary>
    public static IEnumerable<ScannedFile> Scan(string folder, bool recurse, CancellationToken token = default)
    {
        EnumerationOptions options = new()
        {
            RecurseSubdirectories = recurse,
            IgnoreInaccessible = true,
            AttributesToSkip = FileAttributes.System,
            // Bigger OS buffer => fewer syscalls on large directories.
            BufferSize = 16 * 1024,
        };

        FileSystemEnumerable<ScannedFile> files =
            new(folder,
                (ref FileSystemEntry entry) => new ScannedFile(
                    entry.ToFullPath(),
                    entry.FileName.ToString(),
                    entry.Length,
                    entry.LastWriteTimeUtc.LocalDateTime),
                options)
            {
                ShouldIncludePredicate = static (ref FileSystemEntry entry) => !entry.IsDirectory,
            };

        foreach (ScannedFile file in files)
        {
            token.ThrowIfCancellationRequested();
            yield return file;
        }
    }
}
