using System.IO;
using VoiceBridge.App.Platform;
using Xunit;

namespace VoiceBridge.Tests;

public sealed class InputRoutingTests
{
    [Fact]
    public void RestoreOnlyTouchesCaptureRolesStillPointingAtTheAppliedVirtualInput()
    {
        var backup = new InputRouteBackup("virtual", [new("Capture", "Console", "mic-a", "Example A"), new("Capture", "Communications", "mic-b", "Example B")]);
        AudioRoute[] current = [new("Capture", "Console", "virtual", "CABLE Output"), new("Capture", "Communications", "user-new-mic", "User changed it"), new("Render", "Console", "headphones", "Example headphones")];
        var plan = AudioRouting.CaptureRestorePlan(backup, current);
        Assert.Single(plan); Assert.Equal("Capture", plan[0].Flow); Assert.Equal("Console", plan[0].Role); Assert.Equal("mic-a", plan[0].Id);
    }

    [Fact]
    public void InvalidOrPlaybackRoutesAreRejectedWithoutChangingWindowsSettings()
    {
        InputRouteBackup[] backups =
        [new("virtual", [new("Render", "Console", "headphones", "Example")]),
         new("virtual", [new("Capture", "99", "mic", "Example")]),
         new("virtual", [new("Capture", "Console", "mic-a", "Example"), new("Capture", "Console", "mic-b", "Example")])];
        foreach (var backup in backups) Assert.Throws<InvalidDataException>(() => AudioRouting.CaptureRestorePlan(backup, []));
    }

    [Fact]
    public void RestoringAnAlreadyRestoredRouteIsAnEmptyPlan()
    {
        var backup = new InputRouteBackup("virtual", [new("Capture", "Communications", "mic", "Example")]);
        Assert.Empty(AudioRouting.CaptureRestorePlan(backup, [new("Capture", "Communications", "mic", "Example")]));
    }
}
