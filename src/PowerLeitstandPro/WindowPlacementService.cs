using System.Drawing;
using System.Runtime.InteropServices;
using System.Windows;
using System.Windows.Interop;
using Point = System.Drawing.Point;
using Rectangle = System.Drawing.Rectangle;
using Forms = System.Windows.Forms;

namespace PowerControlProWpf;

internal static class WindowPlacementService
{
    private const uint NoSize = 0x0001;
    private const uint NoZOrder = 0x0004;
    private const uint NoActivate = 0x0010;

    internal static System.Windows.Rect GetCursorWorkAreaInDips(Window window)
    {
        var position = Forms.Cursor.Position;
        var area = Forms.Screen.FromPoint(position).WorkingArea;
        var monitor = MonitorFromPoint(position, 2);
        if (monitor != IntPtr.Zero && GetDpiForMonitor(monitor, 0, out var dpiX, out var dpiY) == 0 && dpiX > 0 && dpiY > 0)
            return new System.Windows.Rect(area.Left * 96.0 / dpiX, area.Top * 96.0 / dpiY,
                area.Width * 96.0 / dpiX, area.Height * 96.0 / dpiY);

        var dpi = System.Windows.Media.VisualTreeHelper.GetDpi(window);
        return new System.Windows.Rect(area.Left / dpi.DpiScaleX, area.Top / dpi.DpiScaleY,
            area.Width / dpi.DpiScaleX, area.Height / dpi.DpiScaleY);
    }

    internal static void ApplyOnStartup(Window window, AppSettings settings)
    {
        if (!TryGetBounds(window, out var bounds)) return;
        var preferred = Forms.Screen.FromPoint(Forms.Cursor.Position).WorkingArea;
        var screens = Forms.Screen.AllScreens
            .Select(screen => (screen.DeviceName, screen.WorkingArea)).ToArray();
        var target = WindowPlacementMath.RestoreOrCenter(preferred, screens,
            settings.WindowMonitor, settings.WindowLeftPx, settings.WindowTopPx,
            bounds.Width, bounds.Height);
        SetPosition(window, target);
    }

    internal static void CenterOnCursorScreen(Window window)
    {
        if (!TryGetBounds(window, out var bounds)) return;
        var screen = Forms.Screen.FromPoint(Forms.Cursor.Position);
        SetPosition(window, WindowPlacementMath.Center(screen.WorkingArea, bounds.Width, bounds.Height));
    }

    internal static bool TryRemember(Window window, AppSettings settings)
    {
        if (window.WindowState != WindowState.Normal || !TryGetBounds(window, out var bounds)) return false;
        settings.WindowLeftPx = bounds.Left;
        settings.WindowTopPx = bounds.Top;
        settings.WindowMonitor = Forms.Screen.FromRectangle(bounds).DeviceName;
        return true;
    }

    private static void SetPosition(Window window, Point point)
    {
        var handle = new WindowInteropHelper(window).Handle;
        if (handle == IntPtr.Zero || !SetWindowPos(handle, IntPtr.Zero, point.X, point.Y, 0, 0,
                NoSize | NoZOrder | NoActivate))
            throw new System.ComponentModel.Win32Exception(Marshal.GetLastWin32Error());
    }

    private static bool TryGetBounds(Window window, out Rectangle bounds)
    {
        var handle = new WindowInteropHelper(window).Handle;
        if (handle != IntPtr.Zero && GetWindowRect(handle, out var rect))
        {
            bounds = Rectangle.FromLTRB(rect.Left, rect.Top, rect.Right, rect.Bottom);
            return true;
        }
        bounds = default;
        return false;
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct NativeRect { public int Left, Top, Right, Bottom; }

    [DllImport("user32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool GetWindowRect(IntPtr handle, out NativeRect rect);

    [DllImport("user32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool SetWindowPos(IntPtr handle, IntPtr after, int x, int y,
        int width, int height, uint flags);

    [DllImport("user32.dll")]
    private static extern IntPtr MonitorFromPoint(Point point, uint flags);

    [DllImport("shcore.dll")]
    private static extern int GetDpiForMonitor(IntPtr monitor, int dpiType, out uint dpiX, out uint dpiY);
}
