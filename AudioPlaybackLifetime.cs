using NAudio.Wave;

namespace WellenEditor;

/// <summary>Control-thread ownership of failed retirement; source resources outlive native playback.</summary>
internal sealed class AudioPlaybackLifetime
{
    private sealed class Retirement(IWavePlayer player, IDisposable? source)
    {
        internal readonly IWavePlayer Player = player;
        internal readonly IDisposable? Source = source;
        internal bool NativeClosed;
    }
    private readonly List<Retirement> pending = [];
    internal bool HasPending => pending.Count != 0;
    internal void EnsureReady()
    {
        if (HasPending) throw new InvalidOperationException("Das vorherige Audiogerät ist noch nicht sicher freigegeben. Stopp erneut versuchen; kein neues Gerät wird parallel gestartet.");
    }
    internal Exception? Retire(IWavePlayer player, IDisposable? source)
    {
        var retirement = new Retirement(player, source); pending.Add(retirement); return Complete(retirement);
    }
    internal Exception? Retry()
    {
        Exception? error = null;
        foreach (var retirement in pending.ToArray())
            if (Complete(retirement) is { } next) error = error is null ? next : new AggregateException(error, next);
        return error;
    }
    private Exception? Complete(Retirement retirement)
    {
        Exception? error = null;
        if (!retirement.NativeClosed)
        {
            try { retirement.Player.Stop(); } catch (Exception e) { error = e; }
            try { retirement.Player.Dispose(); retirement.NativeClosed = true; }
            catch (Exception e) { return error is null ? e : new AggregateException(error, e); }
        }
        try { retirement.Source?.Dispose(); pending.Remove(retirement); }
        catch (Exception e) { error = error is null ? e : new AggregateException(error, e); }
        return error;
    }
}
