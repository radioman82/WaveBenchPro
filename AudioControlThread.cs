using System.Windows.Threading;

namespace WellenEditor;

/// <summary>ASIO COM/control calls stay on one STA with a message pump; callbacks never marshal here.</summary>
internal sealed class AudioControlThread : IDisposable
{
    private readonly Thread thread;
    private readonly Dispatcher dispatcher;
    internal AudioControlThread()
    {
        using var ready = new ManualResetEventSlim();
        Dispatcher? created = null;
        thread = new Thread(() => { created = Dispatcher.CurrentDispatcher; ready.Set(); Dispatcher.Run(); })
        { IsBackground = true, Name = "WaveBench ASIO control" };
        thread.SetApartmentState(ApartmentState.STA); thread.Start(); ready.Wait(); dispatcher = created!;
    }
    internal T Invoke<T>(Func<T> work) => dispatcher.Invoke(work);
    internal void Invoke(Action work) => dispatcher.Invoke(work);
    public void Dispose() { dispatcher.BeginInvokeShutdown(DispatcherPriority.Send); if (Thread.CurrentThread != thread) thread.Join(); }
}
