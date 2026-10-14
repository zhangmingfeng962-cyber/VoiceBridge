using System.IO;
using System.Text.Json;
using Microsoft.Win32;

namespace VoiceBridge.App.Platform;

public static class WindowsAudioPreferences
{
    private const string Key = @"Software\Microsoft\Multimedia\Audio";
    private const string ValueName = "UserDuckingPreference";
    public static int? DuckingPreference
    {
        get { using var key = Registry.CurrentUser.OpenSubKey(Key); return key?.GetValue(ValueName) is int value ? value : null; }
    }
    public static string DisableCommunicationDucking()
    {
        using var key = Registry.CurrentUser.CreateSubKey(Key, true);
        bool exists = key.GetValueNames().Contains(ValueName, StringComparer.OrdinalIgnoreCase);
        var backup = new { RegistryKey = "HKEY_CURRENT_USER\\" + Key, ValueName, Existed = exists, OldValue = exists ? key.GetValue(ValueName) : null, OldKind = exists ? key.GetValueKind(ValueName).ToString() : null };
        Directory.CreateDirectory(Settings.Folder);
        string path = Path.Combine(Settings.Folder, $"ducking-before-{DateTimeOffset.Now:yyyyMMdd-HHmmss-fff}.json");
        File.WriteAllText(path, JsonSerializer.Serialize(backup, new JsonSerializerOptions { WriteIndented = true }));
        key.SetValue(ValueName, 3, RegistryValueKind.DWord);
        key.Flush();
        if (DuckingPreference != 3) throw new IOException("Windows 通话音量设置未写入。");
        Log.Write("Windows communication volume attenuation disabled; backup=" + path);
        return path;
    }
}
