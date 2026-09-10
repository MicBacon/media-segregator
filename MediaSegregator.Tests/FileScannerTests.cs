namespace MediaSegregator.Tests;

/// <summary>
/// Which files a scan picks up and how it labels them. Scoped to the extension table — the
/// rest of FileScanner (recursion, sizes, cancellation) is not what these cover.
/// </summary>
public sealed class FileScannerTests : IDisposable
{
    private readonly string _folder;

    public FileScannerTests()
    {
        _folder = Path.Combine(Path.GetTempPath(), "mediasegregator-tests", Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(_folder);
    }

    public void Dispose()
    {
        try
        {
            if (Directory.Exists(_folder))
            {
                Directory.Delete(_folder, recursive: true);
            }
        }
        catch (IOException)
        {
            // A leftover temp folder must never fail an otherwise green test.
        }
    }

    private ScanResult ScanWith(params string[] names)
    {
        foreach (string name in names)
        {
            File.WriteAllText(Path.Combine(_folder, name), "payload");
        }

        return FileScanner.Scan(_folder);
    }

    [Theory]
    [InlineData("photo.jpg")]
    [InlineData("photo.jpeg")]
    [InlineData("screenshot.png")]
    [InlineData("photo.heic")]
    [InlineData("photo.heif")]
    [InlineData("raw.dng")]
    public void Scan_ClassifiesPhotos(string name)
    {
        ScanResult result = ScanWith(name);

        Assert.Equal(MediaKind.Photo, Assert.Single(result.Files).Kind);
        Assert.Equal(1, result.Photos);
        Assert.Equal(0, result.Videos);
    }

    [Theory]
    [InlineData("clip.mp4")]
    [InlineData("clip.mov")]
    [InlineData("clip.3gp")]
    [InlineData("clip.3gpp")]
    [InlineData("clip.mkv")]
    [InlineData("clip.webm")]
    public void Scan_ClassifiesVideos(string name)
    {
        ScanResult result = ScanWith(name);

        Assert.Equal(MediaKind.Video, Assert.Single(result.Files).Kind);
        Assert.Equal(0, result.Photos);
        Assert.Equal(1, result.Videos);
    }

    /// <summary>Cameras write .JPG and .MOV in capitals; the table must not care.</summary>
    [Theory]
    [InlineData("Screenshot.PNG", MediaKind.Photo)]
    [InlineData("IMG_3546.JPG", MediaKind.Photo)]
    [InlineData("IMG_3546.MOV", MediaKind.Video)]
    [InlineData("clip.Mp4", MediaKind.Video)]
    public void Scan_MatchesExtensionsCaseInsensitively(string name, MediaKind expected)
    {
        Assert.Equal(expected, Assert.Single(ScanWith(name).Files).Kind);
    }

    [Theory]
    [InlineData("notes.txt")]
    [InlineData("animation.gif")]
    [InlineData("scan.bmp")]
    [InlineData("movie.avi")]
    [InlineData("settings.json")]
    [InlineData("noextension")]
    public void Scan_IgnoresWhatIsNotCameraMedia(string name)
    {
        Assert.Empty(ScanWith(name).Files);
    }

    [Fact]
    public void Scan_TalliesPhotosAndVideosSeparately()
    {
        ScanResult result = ScanWith("a.png", "b.jpg", "c.mov", "d.mp4", "notes.txt");

        Assert.Equal(4, result.Files.Count);
        Assert.Equal(2, result.Photos);
        Assert.Equal(2, result.Videos);
    }
}
