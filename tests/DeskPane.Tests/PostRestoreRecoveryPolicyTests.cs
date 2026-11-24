using DeskPane.Services;

namespace DeskPane.Tests;

public sealed class PostRestoreRecoveryPolicyTests
{
    [Fact]
    public void ShouldSkipStartupScheduledSnapshot_OnlyOnASuccessfulFullRestore()
    {
        // Only that launch reads raw pre-rebase backup options; a failed
        // apply leaves the original data in place and scoped restores keep
        // the current settings file, so both keep their scheduled snapshot.
        Assert.True(PostRestoreRecoveryPolicy.ShouldSkipStartupScheduledSnapshot(
            DeskPaneRestoreApplyKind.Full, applySucceeded: true));
        Assert.False(PostRestoreRecoveryPolicy.ShouldSkipStartupScheduledSnapshot(
            DeskPaneRestoreApplyKind.Full, applySucceeded: false));
        Assert.False(PostRestoreRecoveryPolicy.ShouldSkipStartupScheduledSnapshot(
            DeskPaneRestoreApplyKind.Scoped, applySucceeded: true));
        Assert.False(PostRestoreRecoveryPolicy.ShouldSkipStartupScheduledSnapshot(
            DeskPaneRestoreApplyKind.Scoped, applySucceeded: false));
        Assert.False(PostRestoreRecoveryPolicy.ShouldSkipStartupScheduledSnapshot(
            DeskPaneRestoreApplyKind.None, applySucceeded: true));
        Assert.False(PostRestoreRecoveryPolicy.ShouldSkipStartupScheduledSnapshot(
            DeskPaneRestoreApplyKind.None, applySucceeded: false));
    }
}
