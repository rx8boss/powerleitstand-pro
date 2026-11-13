using System.IO;
using System.Net;
using System.Text.RegularExpressions;

namespace PowerControlProWpf;

// Download sources are scoped to one product, not merely to a manufacturer's domain.
internal static class ManufacturerDownloadPolicy
{
    internal const string IntelInstaller = "https://dsadata.intel.com/installer";
    internal const string IntelFileName = "Intel-Driver-and-Support-Assistant-Installer.exe";
    internal const string SandiskInstaller = "https://sddashboarddownloads.sandisk.com/wdDashboard/DashboardSetup.exe";
    internal const string MsiInstaller = "https://download.msi.com/uti_exe/desktop/MSI-Center.zip";
    internal static bool HasAdapter(string id) => ManufacturerCatalog.Apps.Any(app => app.Id == id);

    internal static bool Allows(string id, Uri uri)
    {
        if (!uri.IsAbsoluteUri || uri.Scheme != "https" || !uri.IsDefaultPort || uri.UserInfo.Length != 0 ||
            uri.Query.Length != 0 || uri.Fragment.Length != 0 || uri.AbsolutePath.Contains('%')) return false;
        return id switch
        {
            "nvidia" => ManufacturerService.IsAllowedDownload(uri),
            "amd" => uri.Host.Equals("drivers.amd.com", StringComparison.OrdinalIgnoreCase) &&
                Regex.IsMatch(uri.AbsolutePath, @"^/drivers/installer/\d+\.\d+/(?:whql/)?amd-software-adrenalin-edition-\d+\.\d+\.\d+-minimalsetup-\d+_web\.exe$", RegexOptions.IgnoreCase),
            "intel" => uri.AbsoluteUri == IntelInstaller ||
                uri.Host.Equals("downloadmirror.intel.com", StringComparison.OrdinalIgnoreCase) &&
                Regex.IsMatch(uri.AbsolutePath, @"^/\d+/(?:[A-Za-z0-9._-]+/)?Intel-Driver-and-Support-Assistant-Installer\.exe$", RegexOptions.IgnoreCase),
            "kingston" => uri.Host.Equals("media.kingston.com", StringComparison.OrdinalIgnoreCase) &&
                Regex.IsMatch(uri.AbsolutePath, @"^/support/downloads/KSM_setup_1\.5\.\d+\.\d+\.exe$", RegexOptions.IgnoreCase),
            "samsung" => uri.Host.Equals("download.semiconductor.samsung.com", StringComparison.OrdinalIgnoreCase) &&
                Regex.IsMatch(uri.AbsolutePath, @"^/resources/software-resources/Samsung_Magician_Installer_Official_\d+(?:[._]\d+){2,3}\.exe$", RegexOptions.IgnoreCase),
            "sandisk" => uri.AbsoluteUri == SandiskInstaller,
            "asus" => uri.Host.Equals("dlcdnets.asus.com", StringComparison.OrdinalIgnoreCase) && uri.AbsolutePath == "/pub/ASUS/mb/14Utilities/ArmouryCrateInstallTool.zip",
            "msi" => uri.Host.Equals("download.msi.com", StringComparison.OrdinalIgnoreCase) && uri.AbsolutePath == "/uti_exe/desktop/MSI-Center.zip",
            "gigabyte" => uri.Host.Equals("download.gigabyte.com", StringComparison.OrdinalIgnoreCase) &&
                Regex.IsMatch(uri.AbsolutePath, @"^/FileList/Utility/(?:GCC|mb_utility_gcc)[_\d.\-]*\.zip$", RegexOptions.IgnoreCase),
            _ => false
        };
    }

    internal static Uri? ParseDownload(string id, string html)
    {
        var links = Regex.Matches(html, "href\\s*=\\s*[\"'](?<url>[^\"']+)[\"']", RegexOptions.IgnoreCase, TimeSpan.FromSeconds(1))
            .Select(match => WebUtility.HtmlDecode(match.Groups["url"].Value))
            .Select(url => Uri.TryCreate(url, UriKind.Absolute, out var uri) ? uri : null)
            .Where(uri => uri is not null && Allows(id, uri)).Select(uri => uri!).Distinct().ToArray();
        return links.Length == 1 ? links[0] : null;
    }

    internal static Uri AmdReleaseNotes(Uri source)
    {
        if (!Allows("amd", source)) throw new IOException("Die Downloadquelle ist nicht freigegeben.");
        var version = Regex.Match(source.AbsolutePath, @"edition-(\d+)\.(\d+)\.(\d+)-");
        return new($"https://www.amd.com/en/resources/support-articles/release-notes/RN-RAD-WIN-{version.Groups[1]}-{version.Groups[2]}-{version.Groups[3]}.html");
    }

    internal static string PlainText(string html) => Regex.Replace(WebUtility.HtmlDecode(Regex.Replace(html, "<[^>]+>", " ", RegexOptions.None, TimeSpan.FromSeconds(1))), @"\s+", " ");

    internal static string? AmdSupportedGraphics(string graphics, string releaseHtml)
    {
        var text = PlainText(releaseHtml);
        var start = text.IndexOf("Radeon Product Compatibility", StringComparison.OrdinalIgnoreCase);
        var end = text.IndexOf("Mobility Radeon", Math.Max(0, start), StringComparison.OrdinalIgnoreCase);
        if (start < 0 || end <= start) return null;
        var desktopList = text[start..end];
        // Only named desktop RX models. Mobile/OEM/APU/chipset paths need separate support rules.
        foreach (Match model in Regex.Matches(graphics, @"\bRadeon[™®\s]*RX\s+(?<number>\d{4})(?![\w])", RegexOptions.IgnoreCase))
            if (Regex.IsMatch(desktopList, @"(?<!\d)" + model.Groups["number"].Value + @"(?!\d)", RegexOptions.IgnoreCase)) return model.Value;
        return null;
    }

    internal static bool IntelGraphicsSupported(string graphics)
    {
        var normalized = Regex.Replace(graphics, @"\((?:R|TM)\)|[®™]", "", RegexOptions.IgnoreCase);
        return Regex.IsMatch(normalized, @"\bIntel\s+(?:Arc\s+[AB]\d{3}|Iris\s+Xe)\b", RegexOptions.IgnoreCase);
    }
}

internal sealed record ManufacturerHardware(System.Runtime.InteropServices.Architecture Architecture, Version WindowsVersion,
    bool ClientWindows, string Graphics, bool SupportedBrowser, string BoardManufacturer = "", string BoardModel = "");
