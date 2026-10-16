using System.IO;
using System.Text.Json;

namespace VoiceBridge.App.Platform;
public sealed class Settings
{
    public string? MicrophoneId { get; set; }
    public string? CableId { get; set; }
    public double MicrophoneVolume { get; set; } = 100;
    public double MusicVolume { get; set; } = 35;
    public string MusicPlayerId { get; set; } = MusicPlayers.QQMusicId;
    public string ScenarioId { get; set; } = "general";
    public Dictionary<string, ScenarioAudioProfile> ScenarioProfiles { get; set; } = new();
    // Deliberately do not persist music enable or auto-start microphone capture.
    public static string Folder => Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "VoiceBridge");
    public static Settings Load()
    {
        try
        {
            var s = JsonSerializer.Deserialize<Settings>(File.ReadAllText(Path.Combine(Folder, "settings.json"))) ?? new();
            s.Normalize();
            return s;
        }
        catch (Exception ex) when (ex is IOException or JsonException or UnauthorizedAccessException) { return new(); }
    }
    public void Save()
    {
        Normalize();
        Directory.CreateDirectory(Folder);
        string file = Path.Combine(Folder, "settings.json");
        File.WriteAllText(file + ".tmp", JsonSerializer.Serialize(this, new JsonSerializerOptions { WriteIndented = true }));
        if (File.Exists(file)) File.Copy(file, file + ".previous", true);
        File.Move(file + ".tmp", file, true);
    }
    public ScenarioAudioProfile GetScenario(string id)
    {
        ScenarioProfiles ??= new();
        string canonical = Scenarios.Get(id).Id;
        if (!ScenarioProfiles.TryGetValue(canonical, out var profile) || profile is null)
        {
            profile = new ScenarioAudioProfile { MicrophoneId = MicrophoneId, CableId = CableId,
                MicrophoneVolume = MicrophoneVolume, MusicVolume = MusicVolume, MusicPlayerId = MusicPlayerId };
            // Initial values are neutral starting points, not claimed platform-specific corrections.
            ScenarioProfiles[canonical] = profile;
        }
        return profile;
    }
    public void ApplyScenario(ScenarioAudioProfile profile)
    {
        profile.Normalize();
        MicrophoneId = profile.MicrophoneId; CableId = profile.CableId;
        MicrophoneVolume = profile.MicrophoneVolume; MusicVolume = profile.MusicVolume; MusicPlayerId = profile.MusicPlayerId;
    }
    public void InvalidatePeerConfirmations()
    { foreach (var profile in ScenarioProfiles.Values) if (profile is not null) profile.PeerConfirmedSignature = null; }
    public void Normalize()
    {
        ScenarioId = Scenarios.Get(ScenarioId).Id;
        ScenarioProfiles ??= new();
        var normalized = new Dictionary<string, ScenarioAudioProfile>();
        foreach (var pair in ScenarioProfiles)
        {
            if (pair.Value is null || !Scenarios.All.Any(x => string.Equals(x.Id, pair.Key, StringComparison.OrdinalIgnoreCase))) continue;
            pair.Value.Normalize();
            normalized[Scenarios.Get(pair.Key).Id] = pair.Value;
        }
        ScenarioProfiles = normalized;
        ApplyScenario(GetScenario(ScenarioId));
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
