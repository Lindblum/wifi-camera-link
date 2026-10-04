using System.Text.Json;

namespace WiFiCameraLink;

/// <summary>Small per-user settings, saved as JSON next to the log.</summary>
public sealed class Settings
{
    static readonly string FilePath = Path.Combine(AppPaths.AppDir, "settings.json");

    public bool AutoConnect { get; set; }
    public string? LastSsid { get; set; }
    public EnhanceOptions? Enhance { get; set; }
    public CameraType CameraType { get; set; }
    public string? StreamUrl { get; set; }
    /// <summary>Camera networks that have streamed before; only these (and the last one) are joined automatically.</summary>
    public List<string> KnownCameras { get; set; } = [];
    /// <summary>Stream address found by auto-detection, per camera network, so it's tried first next time.</summary>
    public Dictionary<string, string> StreamUrls { get; set; } = [];
    /// <summary>The kind of camera found by auto-detection, per camera network, so it's tried first next time.</summary>
    public Dictionary<string, CameraType> CameraTypes { get; set; } = [];
    /// <summary>VStarcam cameras need their admin password; 888888 is the factory one.</summary>
    public string VStarcamPassword { get; set; } = "888888";

    public static Settings Load()
    {
        try
        {
            if (File.Exists(FilePath))
                return JsonSerializer.Deserialize<Settings>(File.ReadAllText(FilePath)) ?? new Settings();
        }
        catch (Exception ex) when (ex is IOException or JsonException) { }
        return new Settings();
    }

    public void Save()
    {
        try
        {
            Directory.CreateDirectory(AppPaths.AppDir);
            File.WriteAllText(FilePath, JsonSerializer.Serialize(this, new JsonSerializerOptions { WriteIndented = true }));
        }
        catch (IOException) { }
    }
}
