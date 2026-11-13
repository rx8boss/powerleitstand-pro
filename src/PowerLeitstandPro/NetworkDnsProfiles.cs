namespace PowerControlProWpf;

internal sealed record NetworkDnsProfile(string Name, string Ipv4, string Ipv6, string German, string English)
{
    public override string ToString() => Name;

    internal static IReadOnlyList<NetworkDnsProfile> All { get; } =
    [
        new("Cloudflare Standard", "1.1.1.1, 1.0.0.1", "2606:4700:4700::1111, 2606:4700:4700::1001",
            "Allgemeine Namensauflösung ohne Inhaltsfilter.", "General name resolution without content filtering."),
        new("Google Public DNS", "8.8.8.8, 8.8.4.4", "2001:4860:4860::8888, 2001:4860:4860::8844",
            "Allgemeine Namensauflösung.", "General name resolution."),
        new("Quad9", "9.9.9.9, 149.112.112.112", "2620:fe::fe, 2620:fe::9",
            "Filtert bekannte Schadsoftware-Domains.", "Filters known malware domains."),
        new("Cloudflare Malware", "1.1.1.2, 1.0.0.2", "2606:4700:4700::1112, 2606:4700:4700::1002",
            "Filtert bekannte Schadsoftware und Phishing.", "Filters known malware and phishing."),
        new("Cloudflare Family", "1.1.1.3, 1.0.0.3", "2606:4700:4700::1113, 2606:4700:4700::1003",
            "Filtert Schadsoftware, Phishing und Inhalte für Erwachsene.", "Filters malware, phishing and adult content."),
        new("AdGuard DNS", "94.140.14.14, 94.140.15.15", "2a10:50c0::ad1:ff, 2a10:50c0::ad2:ff",
            "Filtert Werbung und Tracker.", "Filters ads and trackers."),
        new("AdGuard Family", "94.140.14.15, 94.140.15.16", "2a10:50c0::bad1:ff, 2a10:50c0::bad2:ff",
            "Filtert Werbung, Tracker und Inhalte für Erwachsene. Aktiviert SafeSearch, wo verfügbar.",
            "Filters ads, trackers and adult content. Enables SafeSearch where available."),
        new("Control D Ads & Tracking", "76.76.2.2, 76.76.10.2", "2606:1a40::2, 2606:1a40:1::2",
            "Filtert Werbung und Tracker.", "Filters ads and trackers."),
        new("DNS4EU", "86.54.11.13, 86.54.11.213", "2a13:1001::86:54:11:13, 2a13:1001::86:54:11:213",
            "Filtert Werbung und bekannte schädliche Domains.", "Filters ads and known malicious domains."),
        new("CleanBrowsing Family", "185.228.168.168, 185.228.169.168", "2a0d:2a00:1::, 2a0d:2a00:2::",
            "Strenger Familienfilter mit SafeSearch. Sperrt auch manche VPN- und Proxy-Domains.",
            "Strict family filter with SafeSearch. Also blocks some VPN and proxy domains.")
    ];
}
