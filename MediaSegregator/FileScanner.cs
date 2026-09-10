using System.IO.Enumeration;

namespace MediaSegregator;

public enum MediaKind
{
    Photo,
    Video,
}

/// <summary>One media file discovered by a scan.</summary>
public sealed record ScannedFile(string Path, string Name, long Length, DateTime Modified, MediaKind Kind);

/// <summary>Result of one scan: the files themselves plus the counts shown in the UI.</summary>
public sealed record ScanResult(IReadOnlyList<ScannedFile> Files, int Photos, int Videos, long TotalBytes)
{
    public static ScanResult Empty { get; } = new([], 0, 0, 0);
}

public static class FileScanner
{
    /// <summary>Folder the executable lives in, regardless of where it was launched from.</summary>
    public static string DefaultFolder => AppContext.BaseDirectory;

    /// <summary>
    /// Container formats a phone camera or screenshot tool can write. JPEG is the default,
    /// HEIC the newer default on recent devices, PNG what screenshots land as and DNG the
    /// RAW option in pro mode; video is MP4 with MOV on iPhones, 3GP on older handsets and
    /// WEBM/MKV on a few OEM camera apps. Matched case-insensitively, so the .JPG and .MOV
    /// spellings a camera writes are picked up alongside the lower-case ones.
    /// </summary>
    private static readonly Dictionary<string, MediaKind> MediaExtensions = new(StringComparer.OrdinalIgnoreCase)
    {
        [".jpg"] = MediaKind.Photo,
        [".jpeg"] = MediaKind.Photo,
        [".png"] = MediaKind.Photo,
        [".heic"] = MediaKind.Photo,
        [".heif"] = MediaKind.Photo,
        [".dng"] = MediaKind.Photo,
        [".mp4"] = MediaKind.Video,
        [".mov"] = MediaKind.Video,
        [".3gp"] = MediaKind.Video,
        [".3gpp"] = MediaKind.Video,
        [".mkv"] = MediaKind.Video,
        [".webm"] = MediaKind.Video,
    };

    /// <summary>
    /// Scans the top level of <paramref name="folder"/> only — subdirectories are not
    /// descended into — and keeps just the camera photo and video formats above.
    /// FileSystemEnumerable reads name, size and timestamps straight out of the
    /// directory-enumeration buffer, so there is no second stat() per file and no
    /// FileInfo allocation; the full path string is only built for entries that pass
    /// the filter.
    /// </summary>
    public static ScanResult Scan(string folder, CancellationToken token = default)
    {
        EnumerationOptions options = new()
        {
            RecurseSubdirectories = false,
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
                    entry.LastWriteTimeUtc.LocalDateTime,
                    KindOf(entry.FileName)!.Value),
                options)
            {
                ShouldIncludePredicate = static (ref FileSystemEntry entry) =>
                    !entry.IsDirectory && KindOf(entry.FileName) is not null,
            };

        List<ScannedFile> found = [];
        int photos = 0;
        int videos = 0;
        long totalBytes = 0;

        foreach (ScannedFile file in files)
        {
            token.ThrowIfCancellationRequested();
            found.Add(file);
            totalBytes += file.Length;

            if (file.Kind == MediaKind.Photo)
            {
                photos++;
            }
            else
            {
                videos++;
            }
        }

        return new ScanResult(found, photos, videos, totalBytes);
    }

    /// <summary>
    /// Classifies by extension without allocating: the name is still a span at this
    /// point, and the lookup runs once per directory entry.
    /// </summary>
    private static MediaKind? KindOf(ReadOnlySpan<char> fileName)
    {
        ReadOnlySpan<char> extension = System.IO.Path.GetExtension(fileName);

        return !extension.IsEmpty && MediaExtensions.GetAlternateLookup<ReadOnlySpan<char>>()
            .TryGetValue(extension, out MediaKind kind)
            ? kind
            : null;
    }
}
