using System;
using System.Runtime.InteropServices;
using System.Windows;
using System.Windows.Interop;
using NAudio.Vst3;

namespace WellenEditor;

/// <summary>WPF HwndHost für die native VST3-Plugin-Oberfläche.</summary>
internal sealed class VstEditorHost : HwndHost
{
    private Vst3PluginView? view;
    private readonly double scale;

    public VstEditorHost(Vst3PluginView view, double scale)
    {
        this.view = view ?? throw new ArgumentNullException(nameof(view));
        this.scale = scale;
        view.Resized += OnResized;
    }

    protected override HandleRef BuildWindowCore(HandleRef hwndParent)
    {
        var handle = CreateWindowEx(
            0, "STATIC", "",
            WS_CHILD | WS_VISIBLE,
            0, 0, 10, 10,
            hwndParent.Handle, IntPtr.Zero, IntPtr.Zero, IntPtr.Zero);

        if (handle == IntPtr.Zero)
            throw new System.ComponentModel.Win32Exception(Marshal.GetLastWin32Error());

        if (view != null)
            view.AttachTo(handle, (float)scale);

        return new HandleRef(this, handle);
    }

    protected override void DestroyWindowCore(HandleRef hwnd)
    {
        if (view != null)
        {
            view.Detach();
            view.Resized -= OnResized;
            view.Dispose();
            view = null;
        }
        if (hwnd.Handle != IntPtr.Zero)
            DestroyWindow(hwnd.Handle);
    }

    private void OnResized(object? sender, Vst3ViewSize size)
    {
        Dispatcher.BeginInvoke(() =>
        {
            Width = size.Width * scale;
            Height = size.Height * scale;
            InvalidateMeasure();
        });
    }

    [DllImport("user32.dll", SetLastError = true, CharSet = CharSet.Unicode)]
    private static extern IntPtr CreateWindowEx(int dwExStyle, string lpClassName, string lpWindowName,
        int dwStyle, int x, int y, int nWidth, int nHeight,
        IntPtr hWndParent, IntPtr hMenu, IntPtr hInstance, IntPtr lpParam);

    [DllImport("user32.dll", SetLastError = true)]
    private static extern bool DestroyWindow(IntPtr hWnd);

    private const int WS_CHILD = 0x40000000;
    private const int WS_VISIBLE = 0x10000000;
}