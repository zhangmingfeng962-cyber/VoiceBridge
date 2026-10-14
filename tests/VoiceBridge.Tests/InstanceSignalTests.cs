using VoiceBridge.App.Platform;
using Xunit;

namespace VoiceBridge.Tests;

[Collection("WpfWindow")]
public sealed class InstanceSignalTests
{
    [Fact]
    public async Task ShowAndExitRequestsReachTheExistingInstance()
    {
        var show = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var exit = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var scope = Guid.NewGuid().ToString("N");
        using var signals = new InstanceSignals(() => show.TrySetResult(), () => exit.TrySetResult(), scope);
        Assert.True(InstanceSignals.ShowExisting(scope));
        await show.Task.WaitAsync(TimeSpan.FromSeconds(3));
        Assert.True(InstanceSignals.ExitExisting(scope));
        await exit.Task.WaitAsync(TimeSpan.FromSeconds(3));
    }
}
