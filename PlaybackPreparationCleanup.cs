namespace WellenEditor;

/// <summary>Every UI restoration step is attempted even if resource cleanup fails.</summary>
internal static class PlaybackPreparationCleanup
{
    public static Exception? Run(Exception? primary, Action cleanup, Action reset, Action enableWorkspace, Action closeProgress)
    {
        var errors = new List<Exception>();
        foreach (var action in new[] { cleanup, reset, enableWorkspace, closeProgress })
            try { action(); } catch (Exception error) { errors.Add(error); }
        if (errors.Count == 0) return primary;
        var aggregate = new AggregateException("Wiedergabevorbereitung konnte nicht vollständig abgeschlossen werden.", errors);
        if (primary is null) return aggregate;
        primary.Data["PlaybackCleanupError"] = aggregate;
        return primary;
    }
}
