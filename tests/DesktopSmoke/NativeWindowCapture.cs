using System.Runtime.InteropServices;
using System.Windows;
using System.Windows.Interop;
using System.Windows.Media.Imaging;

internal static class NativeWindowCapture
{
    [StructLayout(LayoutKind.Sequential)] private struct Bounds { public int Left, Top, Right, Bottom; }
    [StructLayout(LayoutKind.Sequential)] private struct Point { public int X, Y; }
    [DllImport("user32.dll")] private static extern bool GetWindowRect(IntPtr hwnd, out Bounds rect);
    [DllImport("user32.dll")] private static extern bool GetClientRect(IntPtr hwnd, out Bounds rect);
    [DllImport("user32.dll")] private static extern bool ClientToScreen(IntPtr hwnd, ref Point point);
    [DllImport("user32.dll")] private static extern IntPtr GetDC(IntPtr hwnd);
    [DllImport("user32.dll")] private static extern int ReleaseDC(IntPtr hwnd, IntPtr dc);
    [DllImport("user32.dll")] private static extern bool PrintWindow(IntPtr hwnd, IntPtr dc, uint flags);
    [DllImport("user32.dll")] private static extern IntPtr SendMessage(IntPtr hwnd, uint message, IntPtr wParam, IntPtr lParam);
    [DllImport("gdi32.dll")] private static extern IntPtr CreateCompatibleDC(IntPtr dc);
    [DllImport("gdi32.dll")] private static extern IntPtr CreateCompatibleBitmap(IntPtr dc, int width, int height);
    [DllImport("gdi32.dll")] private static extern IntPtr SelectObject(IntPtr dc, IntPtr obj);
    [DllImport("gdi32.dll")] private static extern bool DeleteObject(IntPtr obj);
    [DllImport("gdi32.dll")] private static extern bool DeleteDC(IntPtr dc);

    internal static bool HasNoNativeTopStrip(Window window)
    {
        var hwnd = new WindowInteropHelper(window).Handle;
        var origin = new Point();
        return GetWindowRect(hwnd, out var outer) && GetClientRect(hwnd, out var client) &&
            ClientToScreen(hwnd, ref origin) && origin.Y == outer.Top && origin.X == outer.Left &&
            client.Right == outer.Right - outer.Left && client.Bottom == outer.Bottom - outer.Top;
    }

    internal static int LeftResizeHit(Window window)
    {
        var hwnd = new WindowInteropHelper(window).Handle;
        if (!GetWindowRect(hwnd, out var rect)) throw new InvalidOperationException("GetWindowRect");
        var x = rect.Left + 2; var y = (rect.Top + rect.Bottom) / 2;
        return (int)SendMessage(hwnd, 0x0084, IntPtr.Zero, new IntPtr((y << 16) | (x & 0xffff)));
    }

    internal static BitmapSource Capture(Window window)
    {
        var hwnd = new WindowInteropHelper(window).Handle;
        if (!GetWindowRect(hwnd, out var rect)) throw new InvalidOperationException("GetWindowRect");
        var dc = GetDC(hwnd); var buffer = CreateCompatibleDC(dc);
        var bitmap = CreateCompatibleBitmap(dc, rect.Right - rect.Left, rect.Bottom - rect.Top);
        var previous = SelectObject(buffer, bitmap);
        try
        {
            // Entire native window, including any non-client frame, unlike RenderTargetBitmap.
            if (!PrintWindow(hwnd, buffer, 2)) throw new InvalidOperationException("PrintWindow");
            var source = Imaging.CreateBitmapSourceFromHBitmap(bitmap, IntPtr.Zero, Int32Rect.Empty, BitmapSizeOptions.FromEmptyOptions());
            source.Freeze(); return source;
        }
        finally { SelectObject(buffer, previous); DeleteObject(bitmap); DeleteDC(buffer); ReleaseDC(hwnd, dc); }
    }
}
