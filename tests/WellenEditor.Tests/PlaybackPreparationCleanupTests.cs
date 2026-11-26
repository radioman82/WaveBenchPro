using System.IO;
using WellenEditor;
using Xunit;

namespace WellenEditorTests;

public sealed class PlaybackPreparationCleanupTests
{
    [Fact]
    public void DisposeFailureStillResetsPreparationEnablesWorkspaceAndClosesProgress()
    {
        var preparing = true; var workspaceEnabled = false; var progressOpen = true;
        var primary = new IOException("original preparation failure");
        var result = PlaybackPreparationCleanup.Run(primary,
            () => throw new IOException("injected dispose failure"),
            () => preparing = false, () => workspaceEnabled = true, () => progressOpen = false);
        Assert.Same(primary, result);
        Assert.False(preparing); Assert.True(workspaceEnabled); Assert.False(progressOpen);
        Assert.IsType<AggregateException>(primary.Data["PlaybackCleanupError"]);
    }

    [Fact]
    public void RestorationFailuresAreCollectedWithoutSkippingTheRemainingActions()
    {
        var enabled = false; var closed = false;
        var error = PlaybackPreparationCleanup.Run(null,
            () => throw new IOException("dispose"),
            () => throw new InvalidOperationException("reset"),
            () => enabled = true, () => closed = true);
        Assert.True(enabled); Assert.True(closed);
        Assert.Equal(2, Assert.IsType<AggregateException>(error).InnerExceptions.Count);
    }
}
