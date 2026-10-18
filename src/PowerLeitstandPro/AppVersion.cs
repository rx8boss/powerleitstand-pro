namespace PowerControlProWpf;

internal static class AppVersion
{
    internal static Version Installed
    {
        get
        {
            var value = typeof(AppVersion).Assembly.GetName().Version!;
            return new(value.Major, value.Minor, Math.Max(0, value.Build));
        }
    }
    internal static string UserAgent => "PowerLeitstand-PRO/" + Installed;
}
