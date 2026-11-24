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
            WS_CHILD | WS_VISIBLE | WS_CLIPCHILDREN | WS_CLIPSIBLINGS,
            0, 0, 10, 10,
            hwndParent.Handle, IntPtr.Zero, IntPtr.Zero, IntPtr.Zero);

        if (handle == IntPtr.Zero)
            throw new System.ComponentModel.Win32Exception(Marshal.GetLastWin32Error());

        if (view != null)
        {
            // Vor dem Attach das HWND auf die vom Plugin gemeldete Größe bringen,
            // sonst bekommt CurveEQ-artige Editoren beim ersten Layout eine 10x10-Box.
            var size = view.GetSize();
            SetWindowPos(handle, IntPtr.Zero, 0, 0,
                (int)Math.Max(1, size.Width * scale), (int)Math.Max(1, size.Height * scale),
                SWP_NOZORDER | SWP_NOACTIVATE | SWP_NOMOVE);
            view.AttachTo(handle, (float)scale);
        }

        return new HandleRef(this, handle);
    }

    protected override void DestroyWindowCore(HandleRef hwnd)
    {
        // View-Lebenszyklus liegt beim VstPluginDialog (Detach+Dispose in OnClosed).
        // Hier nur noch das eigene HWND aufräumen – kein doppeltes Dispose der View.
        if (view != null)
        {
            view.Resized -= OnResized;
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

    [DllImport("user32.dll", SetLastError = true)]
    private static extern bool SetWindowPos(IntPtr hWnd, IntPtr hWndInsertAfter, int X, int Y, int cx, int cy, uint uFlags);

    private const int WS_CHILD = 0x40000000;
    private const int WS_VISIBLE = 0x10000000;
    private const int WS_CLIPCHILDREN = 0x02000000;
    private const int WS_CLIPSIBLINGS = 0x04000000;
    private const uint SWP_NOZORDER = 0x0004;
    private const uint SWP_NOACTIVATE = 0x0010;
    private const uint SWP_NOMOVE = 0x0002;
}