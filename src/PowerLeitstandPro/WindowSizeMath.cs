namespace PowerControlProWpf;

internal static class WindowSizeMath
{
    private static readonly (int Index, double Width, double Height)[] Presets =
    [
        (3, 1200, 760),
        (2, 1050, 670),
        (1, 900, 650),
        (0, 860, 620)
    ];

    internal static (int Index, double Width, double Height) SelectAutomatic(double workWidth, double workHeight)
    {
        // Leave a little room around the window and always use an existing layout preset.
        var width = Math.Max(1, workWidth - 24);
        var height = Math.Max(1, workHeight - 24);
        foreach (var preset in Presets)
            if (preset.Width <= width && preset.Height <= height)
                return preset;

        // On a very small or strongly scaled display, retain the compact layout
        // and expose its contents through scrollbars instead of distorting it.
        return (0, Math.Min(860, width), Math.Min(620, height));
    }
}
