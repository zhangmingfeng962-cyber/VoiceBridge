namespace VoiceBridge.App.Platform;

public sealed class InstanceSignals : IDisposable
{
    private readonly EventWaitHandle showEvent, exitEvent;
    private readonly RegisteredWaitHandle showWait, exitWait;

    public InstanceSignals(Action show, Action exit, string? scope = null)
    {
        showEvent = new EventWaitHandle(false, EventResetMode.AutoReset, EventName("Show", scope));
        exitEvent = new EventWaitHandle(false, EventResetMode.AutoReset, EventName("Exit", scope));
        showWait = ThreadPool.RegisterWaitForSingleObject(showEvent, (_, _) => show(), null, Timeout.Infinite, false);
        exitWait = ThreadPool.RegisterWaitForSingleObject(exitEvent, (_, _) => exit(), null, Timeout.Infinite, false);
    }
    public static bool ShowExisting(string? scope = null) => Send(EventName("Show", scope));
    public static bool ExitExisting(string? scope = null) => Send(EventName("Exit", scope));
    private static string EventName(string action, string? scope) => scope is null
        ? "Local\\VoiceBridge.Desktop." + action
        : "Local\\VoiceBridge.Desktop.Test." + Guid.Parse(scope).ToString("N") + "." + action;
    private static bool Send(string name)
    {
        try { using var signal = EventWaitHandle.OpenExisting(name); return signal.Set(); }
        catch (WaitHandleCannotBeOpenedException) { return false; }
    }
    public void Dispose()
    {
        showWait.Unregister(null); exitWait.Unregister(null);
        showEvent.Dispose(); exitEvent.Dispose();
    }
}
