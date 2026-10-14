using System.IO;
using System.Text.Json;

namespace VoiceBridge.App.Platform;
public sealed class Settings
{
    public string? MicrophoneId { get; set; }
    public string? CableId { get; set; }
    public double MicrophoneVolume { get; set; } = 100;
    public double MusicVolume { get; set; } = 35;
    // Deliberately do not persist music enable or auto-start microphone capture.
    public static string Folder => Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "VoiceBridge");
    public static Settings Load()
    {
        try
        {
            var s = JsonSerializer.Deserialize<Settings>(File.ReadAllText(Path.Combine(Folder, "settings.json"))) ?? new();
            s.MicrophoneVolume = double.IsFinite(s.MicrophoneVolume) ? Math.Clamp(s.MicrophoneVolume, 0, 200) : 100;
            s.MusicVolume = double.IsFinite(s.MusicVolume) ? Math.Clamp(s.MusicVolume, 0, 100) : 35;
            return s;
        }
        catch (Exception ex) when (ex is IOException or JsonException or UnauthorizedAccessException) { return new(); }
    }
    public void Save()
    {
        Directory.CreateDirectory(Folder);
        string file = Path.Combine(Folder, "settings.json");
        File.WriteAllText(file + ".tmp", JsonSerializer.Serialize(this, new JsonSerializerOptions { WriteIndented = true }));
        File.Move(file + ".tmp", file, true);
    }
}
public static class Log
{
    private static readonly object Gate = new();
    public static void Write(string message)
    {
        try { lock (Gate) { Directory.CreateDirectory(Settings.Folder); string path = Path.Combine(Settings.Folder, "app.log"); if (File.Exists(path) && new FileInfo(path).Length > 2_000_000) File.Move(path, path + ".previous", true); File.AppendAllText(path, $"{DateTimeOffset.Now:O} {message}\n"); } }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException) { System.Diagnostics.Debug.WriteLine(ex); }
    }
}
