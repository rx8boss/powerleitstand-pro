namespace PowerControlProWpf;

// One palette feeds the main window, dialogs and tray. The first array follows the
// brush keys in MainWindow.ApplyTheme; the second holds semantic action colors.
internal sealed record ThemePalette(
    string[] Colors, string[] Actions, string Ready,
    string? Glow, double GlowOpacity, string FontFamily,
    string? HeaderTextOverride = null, string? HeaderSubtleOverride = null,
    string? FooterTextOverride = null)
{
    internal bool IsNeon => Glow is not null;
    internal string IconSet => IsNeon ? "neon" : "classic";
    internal string HeaderText => HeaderTextOverride ?? Colors[5];
    internal string HeaderSubtle => HeaderSubtleOverride ?? Colors[6];
    internal string FooterText => FooterTextOverride ?? Colors[6];

    internal static string NormalizeName(string? name) => name is
        "Standard" or "Neon Blau" or "Neon Pink" or "Neon Grün" or "Linux Mint" or "Classic" or "Dark"
        ? name : "Standard";

    internal static ThemePalette For(string name) => name switch
    {
        "Neon Blau" => new(
            ["#020A18", "#071C3A", "#04152C", "#06172E", "#020B1E", "#F2FBFF",
             "#A7DDF5", "#19D7FF", "#287C9B", "#0A385A", "#145A76"],
            ["#671333", "#FF4789", "#075B56", "#0C3271", "#262C36", "#FFC95A"],
            "#42EAC0", "#19D7FF", 0.62, "Segoe UI"),
        "Neon Pink" => new(
            ["#10051E", "#2A0C37", "#1A0826", "#1D0B32", "#11051F", "#FFF8FF",
             "#E5BCDD", "#F776D8", "#A84B93", "#54205A", "#703070"],
            ["#751F4D", "#FF6FAA", "#4C2758", "#44225D", "#372437", "#F3B978"],
            "#79E8C6", "#FF56CB", 0.60, "Segoe UI"),
        "Neon Grün" => new(
            ["#021A14", "#07392B", "#062B22", "#062B22", "#041D18", "#F3FFF9",
             "#BCE8D6", "#48EFB3", "#308E72", "#0B5E48", "#168466"],
            ["#5A2D38", "#F2758C", "#0B7351", "#17594D", "#294034", "#E7CB72"],
            "#57E8A4", "#3CF6A4", 0.60, "Segoe UI"),
        "Linux Mint" => new(
            ["#B9C7BC", "#CAD7CC", "#AABBB0", "#D3E0D5", "#E2EBE4", "#23372B",
             "#415E4B", "#265C3F", "#87A791", "#B4D0BA", "#A2C2A9"],
            ["#AA4655", "#8B3440", "#2C6952", "#345F60", "#82545A", "#735C2A"],
            "#286946", null, 0, "Segoe UI", FooterTextOverride: "#23372B"),
        "Classic" => new(
            ["#9ABCEB", "#2869D3", "#2360B8", "#D5E5FA", "#E9F2FF", "#10245A",
             "#355580", "#15489E", "#6192D2", "#AFC9EE", "#C3D9F5"],
            ["#B9423E", "#832C26", "#286AA7", "#315C98", "#8E4948", "#73551F"],
            "#246D39", null, 0, "Tahoma",
            HeaderTextOverride: "#F8FBFF", HeaderSubtleOverride: "#EEF6FF",
            FooterTextOverride: "#EAF3FF"),
        "Dark" => new(
            ["#151A20", "#242B33", "#1E252C", "#252C34", "#1B222A", "#F2F5F8",
             "#BCC7D1", "#A3BECE", "#536779", "#344352", "#485A68"],
            ["#6B3040", "#D97C91", "#2C604F", "#384F68", "#453B3C", "#D1B16A"],
            "#75C996", null, 0, "Segoe UI"),
        _ => new(
            ["#04162B", "#061C36", "#061B34", "#062846", "#04182F", "#F7FBFF",
             "#CBDCF3", "#13C9FF", "#1597D1", "#0D3E6B", "#126196"],
            ["#62283C", "#F06B8B", "#176963", "#174A70", "#252B3A", "#BF9B54"],
            "#29CDA3", null, 0, "Segoe UI")
    };
}
