using System.Text.Json;
using System.Text.RegularExpressions;

namespace VoiceBridge.Core;

public sealed record ProfileSettings
{
    public int Version { get; init; } = 1;
    public string Nickname { get; init; } = "朋友";
    public bool NoiseSuppression { get; init; } = true;
    public static string CleanName(string? name)
    {
        string clean = Regex.Replace(name ?? "", @"[\p{C}]", "").Trim();
        return clean[..Math.Min(20, clean.Length)];
    }
}

public sealed class ProfileStore(string? path = null)
{
    private readonly string file = path ?? Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData), "VoiceBridge", "preferences.json");
    private readonly object gate = new();
    public ProfileSettings Read()
    {
        lock (gate) try
        {
            if (!File.Exists(file) || new FileInfo(file).Length > 4096) return new();
            var value = JsonSerializer.Deserialize<ProfileSettings>(File.ReadAllText(file), Json) ?? new();
            string name = ProfileSettings.CleanName(value.Nickname);
            return value.Version == 1 ? value with { Nickname = name.Length > 0 ? name : "朋友" } : new();
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or JsonException or ArgumentException) { return new(); }
    }
    public bool Save(ProfileSettings value)
    {
        lock (gate)
        {
            string temporary = file + "." + Guid.NewGuid().ToString("N") + ".tmp";
            try
            {
                Directory.CreateDirectory(Path.GetDirectoryName(Path.GetFullPath(file))!);
                string name = ProfileSettings.CleanName(value.Nickname);
                File.WriteAllText(temporary, JsonSerializer.Serialize(value with { Version = 1, Nickname = name.Length > 0 ? name : "朋友" }, Json));
                File.Move(temporary, file, true); return true;
            }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or ArgumentException) { return false; }
            finally { try { if (File.Exists(temporary)) File.Delete(temporary); } catch (IOException) { } catch (UnauthorizedAccessException) { } }
        }
    }
    private static readonly JsonSerializerOptions Json = new() { PropertyNamingPolicy = JsonNamingPolicy.CamelCase, PropertyNameCaseInsensitive = true };
}
