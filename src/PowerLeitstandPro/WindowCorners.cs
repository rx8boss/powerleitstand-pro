using System.Windows;
using System.Windows.Shell;

namespace PowerControlProWpf;

// Let WPF own the native region, including resize, DPI and maximized transitions.
// Opaque windows keep their existing rendering path; layered windows already have
// a transparent surface and a rounded visual frame (for example the tray menu).
internal static class WindowCorners
{
    internal static void Apply(Window window, double radius = 12)
    {
        if (window.AllowsTransparency) return;
        var current = WindowChrome.GetWindowChrome(window);
        var chrome = current is null ? new WindowChrome
        {
            CaptionHeight = 0,
            ResizeBorderThickness = new Thickness(window.ResizeMode == ResizeMode.NoResize ? 0 : 5),
            GlassFrameThickness = new Thickness(0),
            UseAeroCaptionButtons = false,
            NonClientFrameEdges = NonClientFrameEdges.None
        } : (WindowChrome)current.CloneCurrentValue();
        // WindowChromeWorker forwards this value as the GDI ellipse diameter.
        // Convert the visual radius used by our WPF Border/reference preview.
        chrome.CornerRadius = new CornerRadius(radius * 2);
        chrome.GlassFrameThickness = new Thickness(0);
        WindowChrome.SetWindowChrome(window, chrome);
    }
}
