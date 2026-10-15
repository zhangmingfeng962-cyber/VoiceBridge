using System.IO;
using Microsoft.Win32;

namespace VoiceBridge.App.Platform;

public sealed record StartupState(bool Enabled, bool UsesCurrentExecutable);

public sealed class StartupRegistration
{
    public const string RunKeyPath = @"Software\Microsoft\Windows\CurrentVersion\Run";
    public const string ValueName = "VoiceBridge";
    private readonly string keyPath;
    private readonly Func<string> executablePath;

    public StartupRegistration() : this(RunKeyPath,
        () => Environment.ProcessPath ?? throw new InvalidOperationException("无法确定当前 EXE 的位置。")) { }

    // Tests use their own HKCU subtree; they never change the user's actual startup entry.
    public StartupRegistration(string keyPath, Func<string> executablePath)
    {
        this.keyPath = keyPath;
        this.executablePath = executablePath;
    }

    public StartupState Read()
    {
        using var key = Registry.CurrentUser.OpenSubKey(keyPath);
        var command = key?.GetValue(ValueName, null, RegistryValueOptions.DoNotExpandEnvironmentNames) as string;
        bool enabled = !string.IsNullOrWhiteSpace(command);
        if (!enabled) return new(false, false);
        try { return new(true, string.Equals(command, CommandLine(executablePath()), StringComparison.OrdinalIgnoreCase)); }
        catch (InvalidOperationException) { return new(true, false); }
    }

    public void SetEnabled(bool enabled)
    {
        if (enabled)
        {
            string command = CommandLine(executablePath());
            using var key = Registry.CurrentUser.CreateSubKey(keyPath, writable: true)
                ?? throw new InvalidOperationException("无法打开当前用户的启动设置。");
            key.SetValue(ValueName, command, RegistryValueKind.String);
        }
        else
        {
            using var key = Registry.CurrentUser.OpenSubKey(keyPath, writable: true);
            key?.DeleteValue(ValueName, throwOnMissingValue: false);
        }
    }

    public static string CommandLine(string path)
    {
        if (!Path.IsPathFullyQualified(path) || !string.Equals(Path.GetExtension(path), ".exe", StringComparison.OrdinalIgnoreCase)
            || path.IndexOfAny(['"', '\r', '\n', '\0']) >= 0)
            throw new InvalidOperationException("请先将单文件 EXE 放在固定位置，再开启自启动。");
        string command = $"\"{Path.GetFullPath(path)}\" --startup";
        if (command.Length > 260)
            throw new InvalidOperationException("程序路径过长，请将 EXE 放在较短的固定路径后重试。");
        return command;
    }
}
