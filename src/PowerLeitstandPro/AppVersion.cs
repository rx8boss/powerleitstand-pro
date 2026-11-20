using System.Diagnostics;
using System.Reflection;
using System.Text.RegularExpressions;

namespace PowerControlProWpf;

internal enum UpdateChannel { Beta, Stable, Hotfix }
internal enum UpdatePreference { Stable, Hotfixes, Beta }

internal readonly record struct AppBuild(Version Version, UpdateChannel Channel = UpdateChannel.Stable, int Number = 0) : IComparable<AppBuild>
{
    internal static AppBuild? Parse(string? value)
    {
        var m = Regex.Match(value ?? "", @"\Av?(\d+\.\d+\.\d+)(?:-(beta|hotfix)\.([1-9][0-9]*))?\z", RegexOptions.CultureInvariant);
        if (!m.Success || !Version.TryParse(m.Groups[1].Value, out var version)) return null;
        if (!m.Groups[2].Success) return new(version);
        if (!int.TryParse(m.Groups[3].Value, out var number)) return null;
        return new(version, m.Groups[2].Value == "beta" ? UpdateChannel.Beta : UpdateChannel.Hotfix, number);
    }

    public int CompareTo(AppBuild other)
    {
        int version = Version.CompareTo(other.Version);
        if (version != 0) return version;
        int channel = Channel.CompareTo(other.Channel);
        return channel != 0 ? channel : Number.CompareTo(other.Number);
    }

    internal string Display => Channel == UpdateChannel.Stable ? Version.ToString()
        : $"{Version} · {(Channel == UpdateChannel.Beta ? "Beta" : "Hotfix")} {Number}";
    public override string ToString() => Channel == UpdateChannel.Stable ? Version.ToString()
        : $"{Version}-{(Channel == UpdateChannel.Beta ? "beta" : "hotfix")}.{Number}";
}

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
    internal static AppBuild Build { get; } = AppBuild.Parse(
        typeof(AppVersion).Assembly.GetCustomAttribute<AssemblyInformationalVersionAttribute>()?.InformationalVersion.Split('+')[0])
        ?? new(Installed);

    internal static AppBuild? ReadBuild(string path)
    {
        var info = FileVersionInfo.GetVersionInfo(path);
        if (!Version.TryParse(info.FileVersion, out var numeric)) return null;
        var expected = new Version(numeric.Major, numeric.Minor, numeric.Build);
        string? product = info.ProductVersion?.Split('+')[0];
        var build = string.IsNullOrWhiteSpace(product) ? new AppBuild(expected) : AppBuild.Parse(product);
        return build is { } b && b.Version == expected ? b : null;
    }

    internal static string UserAgent => "PowerLeitstand-PRO/" + Build;
}
