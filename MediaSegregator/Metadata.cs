using MetadataExtractor;
using MetadataDirectory = MetadataExtractor.Directory;

namespace MediaSegregator;

/// <summary>
/// One read of everything a media file says about itself, so the routing that wants both the
/// capture date and the coordinates pays for a single pass over the file rather than two.
///
/// Reading twice is what the app used to do — <see cref="MediaDate"/> and
/// <see cref="MediaLocation"/> each opened and parsed the file for themselves — and over a run of
/// a hundred thousand photos that is a hundred thousand file opens and container parses spent for
/// nothing. Nothing here throws: an unrecognised container, a truncated file or a refused read all
/// come back as an empty list, which every reader above already treats as "this file says nothing".
/// </summary>
public static class Metadata
{
    /// <summary>Every metadata directory <paramref name="path"/> carries, or none when it carries none.</summary>
    public static IReadOnlyList<MetadataDirectory> Read(string path)
    {
        try
        {
            return ImageMetadataReader.ReadMetadata(path);
        }
        catch (Exception)
        {
            return [];
        }
    }
}
