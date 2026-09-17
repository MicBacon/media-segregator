using System.Diagnostics;

namespace MediaSegregator;

/// <summary>Tally of one copy run.</summary>
public sealed record CopyResult(
    int Copied,
    int Skipped,
    IReadOnlyList<string> Errors,
    int Undated = 0,
    long BytesCopied = 0);

/// <summary>
/// How far a run has got. Deliberately carries no totals: knowing them would mean counting the
/// sequence up front, and the sequence is walked lazily and exactly once. The caller already knows
/// the totals from the scan that produced the files.
///
/// The two byte figures answer two different questions, and a progress bar wants the second.
/// <paramref name="BytesDone"/> is what actually reached the destination, which is what a cancelled
/// run reports having achieved. <paramref name="BytesSettled"/> is how much of the work the scan
/// measured has been dealt with at all — copied, skipped or failed — so a second run over a sorted
/// library, which copies nothing, still walks its bar from nought to full.
/// </summary>
public sealed record CopyProgress(int FilesDone, long BytesDone, long BytesSettled, string CurrentFile);

/// <summary>
/// Whether a run of <see cref="Required"/> bytes fits where it is headed. <see cref="Available"/>
/// is null when the system will not say — a UNC share has no drive of its own to ask — and an
/// unknown figure is never read as a refusal.
/// </summary>
public sealed record SpaceCheck(long Required, long? Available)
{
    public bool Fits => Available is not { } free || free >= Required;
}

public static class FileCopier
{
    /// <summary>
    /// One megabyte per read. Large enough that a 100 GB video costs a hundred thousand syscalls
    /// rather than twenty-five million, small enough to stay responsive to a cancel.
    /// </summary>
    private const int BufferBytes = 1024 * 1024;

    /// <summary>
    /// Free space is only checked for files this big. Asking the OS once per file would mean a
    /// syscall for each of several thousand photos to guard against something that cannot happen.
    /// </summary>
    private const long FreeSpaceCheckFrom = 1024L * 1024 * 1024;

    /// <summary>An in-flight copy wears this until it is complete, so nothing half-written looks whole.</summary>
    private const string PartialSuffix = ".part";

    /// <summary>Five updates a second is more than the eye needs and far less than a 100 GB file would send.</summary>
    private static readonly TimeSpan ProgressInterval = TimeSpan.FromMilliseconds(200);

    /// <summary>
    /// Copies <paramref name="files"/> into <paramref name="destination"/>, optionally routed into
    /// a subfolder chosen per file by <paramref name="targetFor"/> — pass
    /// <see cref="DestinationLayout.TargetFor"/> for the dated tree, or leave it null to land
    /// everything in the destination root. The originals are left where they are. Subfolders are
    /// created on demand and reused when they already exist. A file already sitting in its target
    /// folder is skipped, as is one whose target folder already holds a file of the same name and
    /// the same length; any other name clash gets a " (n)" suffix, so nothing is ever overwritten.
    /// </summary>
    public static CopyResult Copy(
        IEnumerable<ScannedFile> files,
        string destination,
        Func<ScannedFile, CopyTarget>? targetFor = null,
        CancellationToken token = default,
        IProgress<CopyProgress>? progress = null)
    {
        Directory.CreateDirectory(destination);
        string destinationFull = Path.TrimEndingDirectorySeparator(Path.GetFullPath(destination));

        int copied = 0;
        int skipped = 0;
        int undated = 0;
        long bytesDone = 0;
        long bytesSettled = 0;
        string currentFile = string.Empty;
        List<string> errors = [];

        // One CreateDirectory per folder rather than per file: a run over a few thousand photos
        // otherwise repeats the same handful of syscalls for every single one.
        HashSet<string> created = new(PathComparer);

        // Allocated on the first real copy, so a run that skips everything never takes a megabyte.
        byte[]? buffer = null;
        Stopwatch sinceReport = Stopwatch.StartNew();

        // Closes over the running tallies so the copy loop can report from inside a file as well as
        // between files; without the throttle a 100 GB file would post a hundred thousand updates.
        void Report(bool force)
        {
            if (progress is null || (!force && sinceReport.Elapsed < ProgressInterval))
            {
                return;
            }

            sinceReport.Restart();
            progress.Report(new CopyProgress(copied, bytesDone, bytesSettled, currentFile));
        }

        // Hoisted out of the loop so a run over thousands of files allocates one delegate, not one each.
        Action<long> advanced = chunk =>
        {
            bytesDone += chunk;
            bytesSettled += chunk;
            Report(force: false);
        };

        foreach (ScannedFile file in files)
        {
            token.ThrowIfCancellationRequested();

            // Bytes land in the tally as they stream, which is what makes the bar move inside a
            // 100 GB file; a file that then fails or is cancelled has its partial deleted, so the
            // tally has to be wound back to here or it would count bytes that are on no disk.
            long beforeThisFile = bytesDone;
            long settledBefore = bytesSettled;
            bool cancelled = false;

            try
            {
                string source = Path.GetFullPath(file.Path);
                string folder = destinationFull;
                bool dated = true;
                currentFile = file.Name;

                if (targetFor is not null)
                {
                    CopyTarget target = targetFor(file);
                    dated = target.Dated;

                    folder = Path.TrimEndingDirectorySeparator(
                        Path.GetFullPath(Path.Combine(destinationFull, target.Subfolder)));
                }

                // The run's first sight of this folder, whether it was routed into one or is landing
                // everything in the destination root. CreateDirectory is a no-op the second time for
                // the flat case, and the sweep happens here — before anything is copied in — so it
                // can only ever meet partials an earlier run abandoned.
                if (created.Add(folder))
                {
                    Directory.CreateDirectory(folder);
                    SweepPartials(folder);
                }

                if (string.Equals(Path.GetDirectoryName(source), folder, PathComparison))
                {
                    skipped++;
                    continue;
                }

                if (TargetPath(folder, file.Name, file.Length) is not { } targetPath)
                {
                    skipped++;
                    continue;
                }

                if (file.Length >= FreeSpaceCheckFrom && !HasRoomFor(folder, file.Length))
                {
                    errors.Add($"{file.Name}: za mało miejsca w folderze docelowym");
                    continue;
                }

                buffer ??= GC.AllocateUninitializedArray<byte>(BufferBytes);

                CopyOne(source, targetPath, buffer, token, advanced);
                copied++;

                // Counted only for files that actually copied, so the tally never claims more
                // undated media than the run reports having copied.
                if (!dated)
                {
                    undated++;
                }
            }
            catch (OperationCanceledException)
            {
                // Anuluj pressed while this very file was streaming. The loop is not guaranteed
                // another iteration to notice the token — on the last file there is none — so the
                // cancellation has to step past the per-file collector here.
                cancelled = true;
                bytesDone = beforeThisFile;
                throw;
            }
            catch (Exception ex)
            {
                bytesDone = beforeThisFile;
                errors.Add($"{file.Name}: {ex.Message}");
            }
            finally
            {
                // Whatever became of this file — copied, skipped or failed — the run is now its own
                // length further through the work the scan measured, and the figure is snapped to
                // that length rather than accumulated so a file that grew since the scan cannot
                // push the bar past the end. A cancelled file is the one exception: its partial was
                // deleted, so it was never dealt with at all and the run keeps the ground it held.
                bytesSettled = cancelled ? settledBefore : settledBefore + file.Length;

                // Cancelling forces the report out: the exception carries no tally, so this is the
                // last thing the window will hear about how far the run got.
                if (copied + skipped + errors.Count > 0)
                {
                    Report(force: cancelled);
                }
            }
        }

        // A throttled run can end between intervals, so the finished tally is posted unconditionally
        // — but only once the run has dealt with something, or a run over an empty sequence would
        // report progress it never made.
        if (copied + skipped + errors.Count > 0)
        {
            Report(force: true);
        }

        return new CopyResult(copied, skipped, errors, undated, bytesDone);
    }

    /// <summary>
    /// Streams one file across. <see cref="File.Copy(string, string)"/> would be a line, but it is
    /// a single blocking call: it cannot be cancelled and it cannot say how far it has got, and
    /// neither is acceptable when one file can take a quarter of an hour.
    /// </summary>
    private static void CopyOne(
        string source,
        string target,
        byte[] buffer,
        CancellationToken token,
        Action<long> advanced)
    {
        string partial = target + PartialSuffix;
        long total = 0;

        try
        {
            // bufferSize 0 turns off FileStream's own buffering: the reads below are already a
            // megabyte each, and a second buffer would only copy every byte one more time.
            using (FileStream input = new(source, FileMode.Open, FileAccess.Read, FileShare.Read,
                       bufferSize: 0, FileOptions.SequentialScan))
            using (FileStream output = new(partial, FileMode.Create, FileAccess.Write, FileShare.None,
                       bufferSize: 0, FileOptions.SequentialScan))
            {
                // Reserving the whole length up front keeps a 100 GB file from being laid down
                // across thousands of fragments as it grows.
                output.SetLength(input.Length);

                int read;

                while ((read = input.Read(buffer, 0, buffer.Length)) > 0)
                {
                    token.ThrowIfCancellationRequested();
                    output.Write(buffer, 0, read);
                    total += read;
                    advanced(read);
                }

                // A file that shrank while being read would otherwise keep the zeroes reserved above.
                if (total != output.Length)
                {
                    output.SetLength(total);
                }
            }

            CopyTimestamps(source, partial);

            // Within one directory this is a rename, so the destination never holds a file that
            // looks complete but is not.
            File.Move(partial, target);
        }
        catch (Exception)
        {
            // Cancellation lands here too: the half-written file goes, the exception carries on.
            Delete(partial);
            throw;
        }
    }

    /// <summary>
    /// A stream copy, unlike <see cref="File.Copy(string, string)"/>, carries no timestamps across.
    /// A filesystem that refuses to take them is no reason to throw away a finished copy.
    /// </summary>
    private static void CopyTimestamps(string source, string target)
    {
        try
        {
            File.SetLastWriteTimeUtc(target, File.GetLastWriteTimeUtc(source));
            File.SetCreationTimeUtc(target, File.GetCreationTimeUtc(source));
        }
        catch (Exception)
        {
        }
    }

    private static void Delete(string path)
    {
        try
        {
            File.Delete(path);
        }
        catch (Exception)
        {
            // Best effort: a leftover .part is untidy, but reporting it would mask the real failure.
        }
    }

    /// <summary>
    /// The path to copy to, or null when the copy has already been made. Walks "name.jpg",
    /// "name (1).jpg", … and stops at the first candidate that is free — or that already holds a
    /// file of exactly <paramref name="length"/> bytes, which is what lets a second run over a
    /// sorted library finish without reading a single byte. A directory holding the name counts
    /// as a clash just as a file would.
    /// </summary>
    private static string? TargetPath(string folder, string fileName, long length)
    {
        string candidate = Path.Combine(folder, fileName);
        string stem = Path.GetFileNameWithoutExtension(fileName);
        string extension = Path.GetExtension(fileName);

        for (int i = 1; ; i++)
        {
            if (!Directory.Exists(candidate))
            {
                if (!File.Exists(candidate))
                {
                    return candidate;
                }

                if (new FileInfo(candidate).Length == length)
                {
                    return null;
                }
            }

            candidate = Path.Combine(folder, $"{stem} ({i}){extension}");
        }
    }

    /// <summary>
    /// What the volume behind <paramref name="destination"/> can still take, against the
    /// <paramref name="bytes"/> a run wants to put there. Asking before a run starts turns "no
    /// space left on device" ten minutes into a copy into an instant answer.
    ///
    /// The answer is a guide, not a verdict, and the caller should treat it as one: the figure a
    /// scan produces covers the whole library, while a second run over an already-sorted one
    /// copies almost none of it. Refusing to start on this number would make re-runs impossible on
    /// a full disk, which is the very thing the skip rule exists to make cheap.
    /// </summary>
    public static SpaceCheck RoomFor(string destination, long bytes)
    {
        try
        {
            string? root = Path.GetPathRoot(Path.GetFullPath(destination));

            // DriveInfo wants a drive. A UNC path has a root but not one it recognises, and says so
            // by throwing — which lands below, as "no figure", exactly like an unrooted path.
            return string.IsNullOrEmpty(root)
                ? new SpaceCheck(bytes, null)
                : new SpaceCheck(bytes, new DriveInfo(root).AvailableFreeSpace);
        }
        catch (Exception)
        {
            return new SpaceCheck(bytes, null);
        }
    }

    /// <summary>
    /// The per-file guard, which catches what a preflight cannot: space that went away mid-run,
    /// or another program filling the same disk while this one copies.
    /// </summary>
    private static bool HasRoomFor(string folder, long length) => RoomFor(folder, length).Fits;

    /// <summary>
    /// Deletes partials an earlier run left behind. A cancel or a failure clears up after itself,
    /// but a killed process or a power cut cannot, and the orphan then sits in the destination for
    /// good — invisible to the scanner, which knows no ".part" among its media extensions. Swept
    /// once per folder, on the run's first visit and before anything is copied in, so the run's own
    /// partials are never in range.
    /// </summary>
    private static void SweepPartials(string folder)
    {
        try
        {
            foreach (string entry in Directory.EnumerateFiles(folder))
            {
                // Matched here rather than handed to EnumerateFiles as a "*.part" search pattern:
                // Windows matches those against short 8.3 names too, which quietly widens them to
                // extensions nobody asked about.
                if (entry.EndsWith(PartialSuffix, PathComparison))
                {
                    // A partial belonging to a second instance copying right now is held open with
                    // FileShare.None, so Windows refuses this as a sharing violation and Delete
                    // swallows it — the live copy is safe without a lock of our own.
                    Delete(entry);
                }
            }
        }
        catch (Exception)
        {
            // A folder that will not list is no reason to abandon the copy into it.
        }
    }

    private static StringComparison PathComparison =>
        OperatingSystem.IsWindows() ? StringComparison.OrdinalIgnoreCase : StringComparison.Ordinal;

    private static StringComparer PathComparer =>
        OperatingSystem.IsWindows() ? StringComparer.OrdinalIgnoreCase : StringComparer.Ordinal;
}
