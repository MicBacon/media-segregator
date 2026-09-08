using System.Text.Json;
using System.Text.Json.Serialization;

namespace MediaSegregator;

/// <summary>
/// The handful of values that must survive a restart. Kept as a plain mutable
/// object so loading a file written by an older build simply leaves the missing
/// members at their defaults instead of throwing.
/// </summary>
public sealed class AppSettings
{
    public string? SourceFolder { get; set; }

    public string? DestinationFolder { get; set; }

    public bool Recurse { get; set; }

    [JsonIgnore]
    public static string FilePath { get; } = Path.Combine(
        Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData),
        "MediaSegregator",
        "settings.json");

    /// <summary>
    /// Never throws: a missing, unreadable or malformed file just means "no
    /// preferences yet", which is not worth blocking startup over.
    /// </summary>
    public static AppSettings Load()
    {
        try
        {
            if (File.Exists(FilePath))
            {
                return JsonSerializer.Deserialize<AppSettings>(File.ReadAllText(FilePath)) ?? new AppSettings();
            }
        }
        catch (Exception)
        {
            // Fall through to defaults.
        }

        return new AppSettings();
    }

    /// <summary>Best-effort persist; failures are silent for the same reason.</summary>
    public void Save()
    {
        try
        {
            Directory.CreateDirectory(Path.GetDirectoryName(FilePath)!);
            File.WriteAllText(FilePath, JsonSerializer.Serialize(this, SerializerOptions));
        }
        catch (Exception)
        {
            // Preferences are a convenience, not something to surface as an error.
        }
    }

    private static readonly JsonSerializerOptions SerializerOptions = new() { WriteIndented = true };
}
