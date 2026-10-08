using System.Runtime.InteropServices;
using System.Windows;
using System.Windows.Input;
using System.Windows.Interop;

namespace AtlasWH3.App.Viewport3D;

/// <summary>
/// A child Win32 window for the swap chain (WPF cannot present D3D11 directly). Mouse and keyboard messages the child
/// receives are raised as events in device pixels, since WPF does not route input to hosted HWNDs.
/// </summary>
public sealed class D3DHost : HwndHost
{
    public IntPtr ChildHandle { get; private set; }

    public event Action<IntPtr, int, int>? Created;
    public event Action<int, int>? Resized;
    public event Action<MouseButton, int, int>? MouseDownAt;
    public event Action<MouseButton, int, int>? MouseUpAt;
    public event Action<int, int>? MouseMoveAt;
    public event Action<int, int, int>? Wheel;          // delta, x, y
    public event Action<Key, bool>? KeyChanged;         // key, down
    public event Action? FocusLost;

    protected override HandleRef BuildWindowCore(HandleRef hwndParent)
    {
        ChildHandle = CreateWindowEx(0, "static", "", WS_CHILD | WS_VISIBLE | WS_CLIPCHILDREN, 0, 0,
            Math.Max(1, (int)ActualWidth), Math.Max(1, (int)ActualHeight), hwndParent.Handle, IntPtr.Zero, IntPtr.Zero, IntPtr.Zero);
        // Subclass the static control so we see its messages.
        _proc = Proc;
        _oldProc = SetWindowLongPtr(ChildHandle, GWLP_WNDPROC, Marshal.GetFunctionPointerForDelegate(_proc));
        var size = PixelSize();
        Created?.Invoke(ChildHandle, size.W, size.H);
        return new HandleRef(this, ChildHandle);
    }

    protected override void DestroyWindowCore(HandleRef hwnd) => DestroyWindow(hwnd.Handle);

    protected override void OnRenderSizeChanged(SizeChangedInfo sizeInfo)
    {
        base.OnRenderSizeChanged(sizeInfo);
        var size = PixelSize();
        Resized?.Invoke(size.W, size.H);
    }

    public (int W, int H) PixelSize()
    {
        var dpi = PresentationSource.FromVisual(this)?.CompositionTarget?.TransformToDevice.M11 ?? 1;
        return (Math.Max(1, (int)(ActualWidth * dpi)), Math.Max(1, (int)(ActualHeight * dpi)));
    }

    private delegate IntPtr ChildWndProc(IntPtr hwnd, int msg, IntPtr w, IntPtr l);
    private ChildWndProc? _proc;
    private IntPtr _oldProc;

    private IntPtr Proc(IntPtr hwnd, int msg, IntPtr w, IntPtr l)
    {
        int X() => (short)(l.ToInt64() & 0xFFFF);
        int Y() => (short)((l.ToInt64() >> 16) & 0xFFFF);
        switch (msg)
        {
            case WM_LBUTTONDOWN: SetFocus(hwnd); SetCapture(hwnd); MouseDownAt?.Invoke(MouseButton.Left, X(), Y()); return IntPtr.Zero;
            case WM_RBUTTONDOWN: SetFocus(hwnd); SetCapture(hwnd); MouseDownAt?.Invoke(MouseButton.Right, X(), Y()); return IntPtr.Zero;
            case WM_MBUTTONDOWN: SetFocus(hwnd); SetCapture(hwnd); MouseDownAt?.Invoke(MouseButton.Middle, X(), Y()); return IntPtr.Zero;
            case WM_LBUTTONUP: ReleaseCapture(); MouseUpAt?.Invoke(MouseButton.Left, X(), Y()); return IntPtr.Zero;
            case WM_RBUTTONUP: ReleaseCapture(); MouseUpAt?.Invoke(MouseButton.Right, X(), Y()); return IntPtr.Zero;
            case WM_MBUTTONUP: ReleaseCapture(); MouseUpAt?.Invoke(MouseButton.Middle, X(), Y()); return IntPtr.Zero;
            case WM_MOUSEMOVE: MouseMoveAt?.Invoke(X(), Y()); return IntPtr.Zero;
            case WM_MOUSEWHEEL:
                var pt = new POINT { X = X(), Y = Y() };
                ScreenToClient(hwnd, ref pt); // wheel coordinates are screen coordinates
                Wheel?.Invoke((short)((w.ToInt64() >> 16) & 0xFFFF), pt.X, pt.Y);
                return IntPtr.Zero;
            case WM_KEYDOWN or WM_SYSKEYDOWN:
                KeyChanged?.Invoke(KeyInterop.KeyFromVirtualKey((int)w), true);
                return IntPtr.Zero;
            case WM_KEYUP or WM_SYSKEYUP:
                KeyChanged?.Invoke(KeyInterop.KeyFromVirtualKey((int)w), false);
                return IntPtr.Zero;
            case WM_KILLFOCUS:
                FocusLost?.Invoke();
                break;
            case WM_NCHITTEST:
                // A "static" control reports HTTRANSPARENT, which sends every mouse message to the parent window.
                return (IntPtr)HTCLIENT;
            case WM_MOUSEACTIVATE:
                return (IntPtr)MA_ACTIVATE;
            case WM_GETDLGCODE:
                return (IntPtr)DLGC_WANTALLKEYS;
            case WM_ERASEBKGND:
                return (IntPtr)1;
        }
        return CallWindowProc(_oldProc, hwnd, msg, w, l);
    }

    private const int WS_CHILD = 0x40000000, WS_VISIBLE = 0x10000000, WS_CLIPCHILDREN = 0x02000000, GWLP_WNDPROC = -4;
    private const int WM_LBUTTONDOWN = 0x201, WM_LBUTTONUP = 0x202, WM_RBUTTONDOWN = 0x204, WM_RBUTTONUP = 0x205,
        WM_MBUTTONDOWN = 0x207, WM_MBUTTONUP = 0x208, WM_MOUSEMOVE = 0x200, WM_MOUSEWHEEL = 0x20A, WM_KEYDOWN = 0x100,
        WM_KEYUP = 0x101, WM_SYSKEYDOWN = 0x104, WM_SYSKEYUP = 0x105, WM_GETDLGCODE = 0x87, WM_ERASEBKGND = 0x14, DLGC_WANTALLKEYS = 4,
        WM_NCHITTEST = 0x84, WM_KILLFOCUS = 0x8, HTCLIENT = 1, WM_MOUSEACTIVATE = 0x21, MA_ACTIVATE = 1;

    [StructLayout(LayoutKind.Sequential)] private struct POINT { public int X, Y; }

    [DllImport("user32.dll", CharSet = CharSet.Unicode)]
    private static extern IntPtr CreateWindowEx(int exStyle, string cls, string name, int style, int x, int y, int w, int h, IntPtr parent, IntPtr menu, IntPtr inst, IntPtr param);
    [DllImport("user32.dll")] private static extern bool DestroyWindow(IntPtr hwnd);
    [DllImport("user32.dll")] private static extern IntPtr SetWindowLongPtr(IntPtr hwnd, int index, IntPtr value);
    [DllImport("user32.dll")] private static extern IntPtr CallWindowProc(IntPtr prev, IntPtr hwnd, int msg, IntPtr w, IntPtr l);
    [DllImport("user32.dll")] private static extern IntPtr SetFocus(IntPtr hwnd);
    [DllImport("user32.dll")] private static extern IntPtr SetCapture(IntPtr hwnd);
    [DllImport("user32.dll")] private static extern bool ReleaseCapture();
    [DllImport("user32.dll")] private static extern bool ScreenToClient(IntPtr hwnd, ref POINT p);
}
