using System.Globalization;
using System.Text;

namespace MediaSegregator.Tests;

/// <summary>
/// Fixtures are built byte by byte rather than committed as binaries, so the tests stay
/// self-contained and it is visible in the source exactly what metadata is being read.
/// </summary>
public sealed class MediaDateTests : IDisposable
{
    private readonly string _root;

    public MediaDateTests()
    {
        _root = Path.Combine(Path.GetTempPath(), "mediasegregator-tests", Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(_root);
    }

    public void Dispose()
    {
        try
        {
            if (Directory.Exists(_root))
            {
                Directory.Delete(_root, recursive: true);
            }
        }
        catch (IOException)
        {
            // A leftover temp folder must never fail an otherwise green test.
        }
    }

    // ------------------------------------------------------------- file names

    [Theory]
    [InlineData("IMG_20260301_142233.jpg")]              // Android stock camera
    [InlineData("VID_20260301_142233.mp4")]
    [InlineData("PXL_20260301_142233123.jpg")]           // Pixel, milliseconds appended
    [InlineData("IMG-20260301-WA0001.jpg")]              // WhatsApp
    [InlineData("VID-20260301-WA0002.mp4")]
    [InlineData("20260301_142233.jpg")]                  // Samsung
    [InlineData("2026-03-01 14.22.33.jpg")]              // iOS export
    [InlineData("2026_03_01.jpg")]
    [InlineData("2026.03.01.heic")]
    [InlineData("Signal-2026-03-01-142233.jpg")]
    [InlineData("00000IMG_00000_BURST20260301142233_COVER.jpg")]
    [InlineData("20260301142233.mp4")]
    [InlineData("Screenshot_20260301-142233.png")]        // Android screenshot
    [InlineData("Screenshot 2026-03-01 at 14.22.33.png")] // macOS screenshot
    [InlineData("Zrzut ekranu 2026-03-1 o 17.09.31.png")] // macOS pl, unpadded day
    [InlineData("Zrzut ekranu 2026-3-01 o 17.09.31.png")] // unpadded month
    [InlineData("Zrzut ekranu 2026-3-1 o 17.09.31.png")]  // both unpadded
    public void FromFileName_ReadsTheDateCameraAppsBakeIn(string name)
    {
        Assert.Equal(new DateTime(2026, 3, 1), MediaDate.FromFileName(name));
    }

    /// <summary>The unpadded patterns must not stop short and read 2026-06-15 as the 1st.</summary>
    [Fact]
    public void FromFileName_TakesTheWholeDay_WhenItIsTwoDigits()
    {
        Assert.Equal(new DateTime(2024, 6, 15), MediaDate.FromFileName("Zrzut ekranu 2024-06-15 o 17.09.31.png"));
    }

    [Theory]
    [InlineData("1740835200000.jpg")]        // epoch milliseconds, not a date
    [InlineData("IMG_1234.HEIC")]            // iPhone, no date in the name
    [InlineData("whatsapp-blob-8842.jpg")]
    [InlineData("IMG_20261301_142233.jpg")]  // month 13
    [InlineData("IMG_20260230_142233.jpg")]  // 30 February
    [InlineData("IMG_18990301_142233.jpg")]  // before photography went digital
    [InlineData("IMG_20990301_142233.jpg")]  // the future
    [InlineData(".hidden")]
    [InlineData("")]
    public void FromFileName_RejectsWhatIsNotADate(string name)
    {
        Assert.Null(MediaDate.FromFileName(name));
    }

    [Fact]
    public void FromFileName_SkipsAnImplausibleMatchAndKeepsLooking()
    {
        // The 1899 run matches the pattern but fails the sanity window; the later one is real.
        Assert.Equal(new DateTime(2026, 3, 1), MediaDate.FromFileName("18990101_IMG_20260301_142233.jpg"));
    }

    // -------------------------------------------------------------- metadata

    [Fact]
    public void Taken_ReadsExifDateTimeOriginal()
    {
        ScannedFile file = Media("photo.jpg", JpegWithDateTimeOriginal(new DateTime(2026, 3, 1, 14, 22, 33)));

        Assert.Equal(new DateTime(2026, 3, 1), MediaDate.Taken(file)!.Value.Date);
    }

    [Fact]
    public void Taken_ReadsQuickTimeMovieHeaderCreationTime()
    {
        ScannedFile file = Media(
            "clip.mp4",
            Mp4WithCreationTime(new DateTime(2025, 3, 1, 14, 22, 33)),
            MediaKind.Video);

        Assert.Equal(new DateTime(2025, 3, 1), MediaDate.Taken(file)!.Value.Date);
    }

    /// <summary>
    /// .mov is the container iPhones write, and the reader sniffs content rather than the
    /// extension — so the QuickTime path has to work under either name.
    /// </summary>
    [Fact]
    public void Taken_ReadsQuickTimeCreationTime_FromAMovFile()
    {
        ScannedFile file = Media(
            "IMG_3546.MOV",
            Mp4WithCreationTime(new DateTime(2025, 7, 30, 9, 5, 0)),
            MediaKind.Video);

        Assert.Equal(new DateTime(2025, 7, 30), MediaDate.Taken(file)!.Value.Date);
    }

    [Fact]
    public void Taken_PrefersMetadataOverTheFileName()
    {
        ScannedFile file = Media(
            "IMG_20200115_010101.jpg",
            JpegWithDateTimeOriginal(new DateTime(2026, 3, 1, 14, 22, 33)));

        Assert.Equal(new DateTime(2026, 3, 1), MediaDate.Taken(file)!.Value.Date);
    }

    [Fact]
    public void Taken_IgnoresAMetadataDateFromADeadCameraClock()
    {
        ScannedFile file = Media(
            "IMG_20260301_142233.jpg",
            JpegWithDateTimeOriginal(new DateTime(1980, 1, 1, 0, 0, 0)));

        Assert.Equal(new DateTime(2026, 3, 1), MediaDate.Taken(file)!.Value.Date);
    }

    // -------------------------------------------------------------- fallbacks

    [Fact]
    public void Taken_FallsBackToTheFileName_WhenTheFileIsNotReadableMedia()
    {
        ScannedFile file = Media("IMG_20260301_142233.jpg", Encoding.ASCII.GetBytes("this is not a JPEG"));

        Assert.Equal(new DateTime(2026, 3, 1), MediaDate.Taken(file)!.Value.Date);
    }

    [Fact]
    public void Taken_FallsBackToTheFileName_ForMatroskaWhichHasNoMetadataReader()
    {
        ScannedFile file = Media("clip_20260301_142233.mkv", Encoding.ASCII.GetBytes("fake"), MediaKind.Video);

        Assert.Equal(new DateTime(2026, 3, 1), MediaDate.Taken(file)!.Value.Date);
    }

    [Fact]
    public void Taken_ReturnsNull_WhenNeitherMetadataNorNameKnows()
    {
        ScannedFile file = Media("whatsapp-blob-8842.jpg", Encoding.ASCII.GetBytes("neither"));

        Assert.Null(MediaDate.Taken(file));
    }

    [Fact]
    public void Taken_ReturnsNull_WhenTheFileIsMissing()
    {
        ScannedFile ghost = new(
            Path.Combine(_root, "gone.jpg"), "gone.jpg", 0, DateTime.Now, MediaKind.Photo);

        Assert.Null(MediaDate.Taken(ghost));
    }

    [Fact]
    public void Taken_NeverUsesTheFileSystemTimestamp()
    {
        // The whole point: a file copied off a phone today must not be filed under today.
        ScannedFile file = Media("whatsapp-blob-8842.jpg", Encoding.ASCII.GetBytes("neither"));

        Assert.Null(MediaDate.Taken(file));
        Assert.NotEqual(default, File.GetLastWriteTime(file.Path));
    }

    // ---------------------------------------------------------------- helpers

    private ScannedFile Media(string name, byte[] content, MediaKind kind = MediaKind.Photo)
    {
        string path = Path.Combine(_root, name);
        File.WriteAllBytes(path, content);

        return new ScannedFile(path, name, content.Length, DateTime.Now, kind);
    }

    private static void U16(List<byte> to, int value)
    {
        to.Add((byte)value);
        to.Add((byte)(value >> 8));
    }

    private static void U32(List<byte> to, int value)
    {
        to.Add((byte)value);
        to.Add((byte)(value >> 8));
        to.Add((byte)(value >> 16));
        to.Add((byte)(value >> 24));
    }

    private static void B16(List<byte> to, int value)
    {
        to.Add((byte)(value >> 8));
        to.Add((byte)value);
    }

    private static void B32(List<byte> to, long value)
    {
        to.Add((byte)(value >> 24));
        to.Add((byte)(value >> 16));
        to.Add((byte)(value >> 8));
        to.Add((byte)value);
    }

    /// <summary>
    /// The smallest JPEG that carries an EXIF DateTimeOriginal: SOI, an APP1 segment holding a
    /// little-endian TIFF block whose IFD0 has a single entry pointing at a sub-IFD, which in turn
    /// has a single entry pointing at the 20-byte ASCII timestamp, then EOI. No image data — the
    /// metadata reader never looks for any.
    /// </summary>
    private static byte[] JpegWithDateTimeOriginal(DateTime taken)
    {
        const int Ifd0Offset = 8;
        const int EntrySize = 12;
        const int IfdSize = 2 + EntrySize + 4;                 // entry count, one entry, next-IFD pointer
        const int SubIfdOffset = Ifd0Offset + IfdSize;
        const int TimestampOffset = SubIfdOffset + IfdSize;

        List<byte> tiff = [(byte)'I', (byte)'I'];              // little-endian byte order
        U16(tiff, 42);                                          // TIFF magic
        U32(tiff, Ifd0Offset);

        U16(tiff, 1);
        Entry(tiff, tag: 0x8769, type: 4, count: 1, value: SubIfdOffset);   // Exif IFD pointer, LONG
        U32(tiff, 0);                                           // no IFD1

        U16(tiff, 1);
        Entry(tiff, tag: 0x9003, type: 2, count: 20, value: TimestampOffset); // DateTimeOriginal, ASCII
        U32(tiff, 0);

        tiff.AddRange(Encoding.ASCII.GetBytes(taken.ToString("yyyy:MM:dd HH:mm:ss", CultureInfo.InvariantCulture)));
        tiff.Add(0);

        List<byte> jpeg = [0xFF, 0xD8, 0xFF, 0xE1];             // SOI, APP1
        B16(jpeg, 2 + 6 + tiff.Count);                          // segment length, including itself
        jpeg.AddRange(Encoding.ASCII.GetBytes("Exif"));
        jpeg.Add(0);
        jpeg.Add(0);
        jpeg.AddRange(tiff);
        jpeg.AddRange([0xFF, 0xD9]);                            // EOI

        return [.. jpeg];

        static void Entry(List<byte> to, int tag, int type, int count, int value)
        {
            U16(to, tag);
            U16(to, type);
            U32(to, count);
            U32(to, value);
        }
    }

    /// <summary>
    /// The smallest MP4 that carries a creation time: an ftyp box — which is what the reader
    /// sniffs to recognise the container — followed by a moov holding a version-0 mvhd.
    /// </summary>
    private static byte[] Mp4WithCreationTime(DateTime created)
    {
        long seconds = (long)(created - new DateTime(1904, 1, 1)).TotalSeconds;

        List<byte> mvhd = [];
        B32(mvhd, 108);                                         // box size
        mvhd.AddRange(Encoding.ASCII.GetBytes("mvhd"));
        mvhd.AddRange([0, 0, 0, 0]);                            // version 0, no flags
        B32(mvhd, seconds);                                     // creation_time
        B32(mvhd, seconds);                                     // modification_time
        B32(mvhd, 1000);                                        // timescale
        B32(mvhd, 0);                                           // duration
        B32(mvhd, 0x00010000);                                  // rate 1.0
        B16(mvhd, 0x0100);                                      // volume 1.0
        mvhd.AddRange(new byte[2 + 8]);                         // reserved
        mvhd.AddRange(new byte[36]);                            // display matrix
        mvhd.AddRange(new byte[24]);                            // predefined
        B32(mvhd, 2);                                           // next_track_ID

        List<byte> mp4 = [];
        B32(mp4, 20);
        mp4.AddRange(Encoding.ASCII.GetBytes("ftypisom"));
        B32(mp4, 0x200);                                        // minor version
        mp4.AddRange(Encoding.ASCII.GetBytes("isom"));           // compatible brands

        B32(mp4, 8 + mvhd.Count);
        mp4.AddRange(Encoding.ASCII.GetBytes("moov"));
        mp4.AddRange(mvhd);

        return [.. mp4];
    }
}
