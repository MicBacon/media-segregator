using System.Text;

namespace MediaSegregator.Tests;

/// <summary>
/// Fixtures are built byte by byte rather than committed as binaries, so the tests stay
/// self-contained and it is visible in the source exactly what metadata is being read.
///
/// The video fixture uses the moov/udta/©xyz atom Android writes. Apple's
/// com.apple.quicktime.location.ISO6709 lives in a moov/meta keys-and-ilst structure several boxes
/// deep and has no fixture here: MetadataExtractor surfaces both under the same tag, so the string
/// it yields is the one the ISO 6709 theories below already cover.
/// </summary>
public sealed class MediaLocationTests : IDisposable
{
    private readonly string _root;

    public MediaLocationTests()
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

    // ------------------------------------------------------------- EXIF stills

    [Fact]
    public void Of_ReadsTheExifGpsFix()
    {
        ScannedFile file = Media("IMG_0001.jpg",
            JpegWithGps('N', [(52, 1), (13, 1), (4692, 100)], 'E', [(21, 1), (0, 1), (4392, 100)]));

        GeoPoint point = MediaLocation.Of(file)!.Value;

        Assert.Equal(52.2297, point.Latitude, 4);
        Assert.Equal(21.0122, point.Longitude, 4);
    }

    /// <summary>EXIF stores the hemisphere separately, so a southern fix is S plus a positive value.</summary>
    [Fact]
    public void Of_ReadsSouthernAndWesternHemispheresAsNegative()
    {
        ScannedFile file = Media("IMG_0002.jpg",
            JpegWithGps('S', [(33, 1), (52, 1), (0, 1)], 'W', [(70, 1), (40, 1), (0, 1)]));

        GeoPoint point = MediaLocation.Of(file)!.Value;

        Assert.Equal(-33.8667, point.Latitude, 4);
        Assert.Equal(-70.6667, point.Longitude, 4);
    }

    /// <summary>Exactly (0, 0) is what a camera writes when location services never got a fix.</summary>
    [Fact]
    public void Of_IgnoresAnAllZeroFix()
    {
        ScannedFile file = Media("IMG_0003.jpg",
            JpegWithGps('N', [(0, 1), (0, 1), (0, 1)], 'E', [(0, 1), (0, 1), (0, 1)]));

        Assert.Null(MediaLocation.Of(file));
    }

    [Fact]
    public void Of_ReturnsNull_WhenThePhotoCarriesNoGps()
    {
        ScannedFile file = Media("screenshot.png", Encoding.ASCII.GetBytes("not really an image"));

        Assert.Null(MediaLocation.Of(file));
    }

    [Fact]
    public void Of_ReturnsNull_WhenTheFileIsMissing()
    {
        ScannedFile ghost = new(Path.Combine(_root, "gone.jpg"), "gone.jpg", 0, DateTime.Now, MediaKind.Photo);

        Assert.Null(MediaLocation.Of(ghost));
    }

    // ---------------------------------------------------------- Android video

    /// <summary>
    /// Android camera apps put the fix in moov/udta/©xyz, which MetadataExtractor has no reader
    /// for — this is the case the hand-rolled atom scan exists to serve.
    /// </summary>
    [Fact]
    public void Of_ReadsTheLocationAndroidWritesIntoUserData()
    {
        ScannedFile file = Media("VID_20260301_142233.mp4", Mp4WithUserDataLocation("+52.2297+021.0122/"),
            MediaKind.Video);

        GeoPoint point = MediaLocation.Of(file)!.Value;

        Assert.Equal(52.2297, point.Latitude, 4);
        Assert.Equal(21.0122, point.Longitude, 4);
    }

    [Fact]
    public void Of_ReadsUserDataLocationWithAnAltitude()
    {
        ScannedFile file = Media("clip.mp4", Mp4WithUserDataLocation("+52.2297+021.0122+100.000/"), MediaKind.Video);

        Assert.Equal(52.2297, MediaLocation.Of(file)!.Value.Latitude, 4);
    }

    [Fact]
    public void Of_ReturnsNull_WhenTheVideoCarriesNoUserDataLocation()
    {
        ScannedFile file = Media("clip.mp4", Encoding.ASCII.GetBytes("not a container"), MediaKind.Video);

        Assert.Null(MediaLocation.Of(file));
    }

    /// <summary>
    /// The reader sniffs content rather than the extension, so a video saved under a photo's name
    /// still gives up its fix — MediaKind is never consulted.
    /// </summary>
    [Fact]
    public void Of_ReadsTheFixByContent_NotByExtension()
    {
        ScannedFile file = Media("mislabelled.jpg", Mp4WithUserDataLocation("+52.2297+021.0122/"));

        Assert.Equal(52.2297, MediaLocation.Of(file)!.Value.Latitude, 4);
    }

    // ---------------------------------------------------------------- ISO 6709

    [Theory]
    [InlineData("+52.2297+021.0122/", 52.2297, 21.0122)]          // Android, no altitude
    [InlineData("+52.2297+021.0122+100.000/", 52.2297, 21.0122)]  // iPhone, with altitude
    [InlineData("+52.2297+021.0122", 52.2297, 21.0122)]           // no closing solidus
    [InlineData("-33.8667-070.6667/", -33.8667, -70.6667)]        // southern and western
    [InlineData("+52+021/", 52, 21)]                              // whole degrees
    [InlineData("  +52.2297+021.0122/  ", 52.2297, 21.0122)]      // padded
    public void FromIso6709_ReadsTheCoordinatesCamerasWrite(string raw, double latitude, double longitude)
    {
        GeoPoint point = MediaLocation.FromIso6709(raw)!.Value;

        Assert.Equal(latitude, point.Latitude, 4);
        Assert.Equal(longitude, point.Longitude, 4);
    }

    [Theory]
    [InlineData(null)]
    [InlineData("")]
    [InlineData("   ")]
    [InlineData("+0.0000+000.0000/")]     // no fix, not the Gulf of Guinea
    [InlineData("+5213.7+02100.7/")]      // packed degrees-minutes, deliberately not guessed at
    [InlineData("52.2297,21.0122")]       // a decimal pair, but not ISO 6709
    [InlineData("+91.0000+021.0122/")]    // off the globe
    [InlineData("+52.2297+181.0000/")]
    [InlineData("Warszawa")]
    public void FromIso6709_RejectsWhatIsNotAFix(string? raw)
    {
        Assert.Null(MediaLocation.FromIso6709(raw));
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
    /// The smallest JPEG that carries a GPS fix: SOI, an APP1 segment holding a little-endian TIFF
    /// block whose IFD0 has a single entry pointing at a GPS IFD, which holds the hemisphere
    /// letters inline and the degrees, minutes and seconds as rationals out in the data area.
    /// No image data — the metadata reader never looks for any.
    /// </summary>
    private static byte[] JpegWithGps(
        char latitudeRef, (int Numerator, int Denominator)[] latitude,
        char longitudeRef, (int Numerator, int Denominator)[] longitude)
    {
        const int Ifd0Offset = 8;
        const int EntrySize = 12;
        const int Ifd0Size = 2 + EntrySize + 4;                    // entry count, one entry, next-IFD pointer
        const int GpsIfdOffset = Ifd0Offset + Ifd0Size;
        const int GpsIfdSize = 2 + (4 * EntrySize) + 4;
        const int LatitudeOffset = GpsIfdOffset + GpsIfdSize;
        const int LongitudeOffset = LatitudeOffset + 24;           // three rationals of eight bytes

        List<byte> tiff = [(byte)'I', (byte)'I'];                  // little-endian byte order
        U16(tiff, 42);                                              // TIFF magic
        U32(tiff, Ifd0Offset);

        U16(tiff, 1);
        Entry(tiff, tag: 0x8825, type: 4, count: 1, value: GpsIfdOffset);   // GPS IFD pointer, LONG
        U32(tiff, 0);                                               // no IFD1

        // Entries within an IFD are ordered by tag, which is what a reader relies on.
        U16(tiff, 4);
        Entry(tiff, tag: 0x0001, type: 2, count: 2, value: latitudeRef);    // GPSLatitudeRef, ASCII "N\0"
        Entry(tiff, tag: 0x0002, type: 5, count: 3, value: LatitudeOffset); // GPSLatitude, RATIONAL
        Entry(tiff, tag: 0x0003, type: 2, count: 2, value: longitudeRef);
        Entry(tiff, tag: 0x0004, type: 5, count: 3, value: LongitudeOffset);
        U32(tiff, 0);

        Rationals(tiff, latitude);
        Rationals(tiff, longitude);

        List<byte> jpeg = [0xFF, 0xD8, 0xFF, 0xE1];                 // SOI, APP1
        B16(jpeg, 2 + 6 + tiff.Count);                              // segment length, including itself
        jpeg.AddRange(Encoding.ASCII.GetBytes("Exif"));
        jpeg.Add(0);
        jpeg.Add(0);
        jpeg.AddRange(tiff);
        jpeg.AddRange([0xFF, 0xD9]);                                // EOI

        return [.. jpeg];

        static void Entry(List<byte> to, int tag, int type, int count, int value)
        {
            U16(to, tag);
            U16(to, type);
            U32(to, count);
            U32(to, value);
        }

        static void Rationals(List<byte> to, (int Numerator, int Denominator)[] parts)
        {
            foreach ((int numerator, int denominator) in parts)
            {
                U32(to, numerator);
                U32(to, denominator);
            }
        }
    }

    /// <summary>
    /// An MP4 whose moov holds a udta with a ©xyz text box — the shape Android's recorder writes.
    /// The text box leads with its own byte count and a language code before the string itself.
    /// </summary>
    private static byte[] Mp4WithUserDataLocation(string iso6709)
    {
        byte[] text = Encoding.UTF8.GetBytes(iso6709);

        List<byte> xyz = [];
        B32(xyz, 8 + 4 + text.Length);
        xyz.AddRange([0xA9, (byte)'x', (byte)'y', (byte)'z']);
        B16(xyz, text.Length);
        B16(xyz, 0x15C7);                                           // language code, unused here
        xyz.AddRange(text);

        List<byte> udta = [];
        B32(udta, 8 + xyz.Count);
        udta.AddRange(Encoding.ASCII.GetBytes("udta"));
        udta.AddRange(xyz);

        List<byte> mp4 = [];
        B32(mp4, 20);
        mp4.AddRange(Encoding.ASCII.GetBytes("ftypisom"));
        B32(mp4, 0x200);                                            // minor version
        mp4.AddRange(Encoding.ASCII.GetBytes("isom"));               // compatible brands

        B32(mp4, 8 + udta.Count);
        mp4.AddRange(Encoding.ASCII.GetBytes("moov"));
        mp4.AddRange(udta);

        return [.. mp4];
    }
}
