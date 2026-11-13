using System.IO;
using System.Text.Json;

namespace PowerControlProWpf;

internal sealed record ProgramEntry(string Id, string Name, string[] Categories, string? PackageId,
    string Publisher, string DescriptionDe, string DescriptionEn, string Website, string IconUrl,
    string? PreviewUrl, string? PackageEvidence, string AssetVersion, string? UseDe = null, string? UseEn = null, string[]? InstalledNames = null)
{
    internal string Description(bool english) => english ? DescriptionEn : DescriptionDe;
    internal string Purpose(bool english) => (english ? UseEn : UseDe) ?? ProgramCatalog.Purpose(Categories[0], english);
}

internal static class ProgramCatalog
{
    internal static readonly ProgramEntry[] Entries = Load();
    private static ProgramEntry[] Load()
    {
        using var stream = typeof(ProgramCatalog).Assembly.GetManifestResourceStream("PowerControlProWpf.ProgramCatalog.json")
            ?? throw new InvalidDataException("Program catalog missing");
        return JsonSerializer.Deserialize<ProgramEntry[]>(stream) ?? [];
    }
    internal static bool Contains(ProgramEntry item) => Entries.Any(x => x.Id == item.Id && x == item);
    internal static readonly string[] Categories = ["all", "browser", "tools", "development", "media", "streaming", "ai", "gaming", "office", "communication", "network"];
    internal static readonly string[] AdditionalCategories = ["office", "communication", "network"];
    internal static string Category(string id, bool english) => (id, english) switch
    {
        ("all", false) => "Alle", ("all", true) => "All", ("browser", _) => "Browser", ("tools", _) => "Tools",
        ("development", false) => "Entwicklung", ("development", true) => "Development",
        ("office", false) => "Büro / PDF", ("office", true) => "Office / PDF",
        ("communication", false) => "Kommunikation", ("communication", true) => "Communication",
        ("network", false) => "Netzwerk / Server", ("network", true) => "Network / Servers",
        ("media", false) => "Medien", ("media", true) => "Media", ("streaming", _) => "Streaming", ("gaming", _) => "Gaming", ("online", _) => "WinGet", _ => english ? "AI" : "KI"
    };
    internal static string Purpose(string category, bool english) => (category, english) switch
    {
        ("browser", false) => "Zum Surfen, Recherchieren und Verwenden von Webanwendungen.",
        ("browser", true) => "For browsing, research and web applications.",
        ("development", false) => "Für Softwareentwicklung, Projektarbeit und technische Arbeitsabläufe.",
        ("development", true) => "For software development, project work and technical workflows.",
        ("media", false) => "Für die Wiedergabe oder kreative Bearbeitung von Medien, entsprechend den beschriebenen Funktionen.",
        ("media", true) => "For media playback or creative editing, according to the functions described.",
        ("streaming", false) => "Für Kommunikation, Aufnahme oder Produktion von Streams, entsprechend der Programmbeschreibung.",
        ("streaming", true) => "For communication, recording or stream production, according to the program description.",
        ("ai", false) => "Zum Arbeiten mit KI. Benötigte Modelle, Konten und passende Hardware beim Anbieter prüfen.",
        ("ai", true) => "For working with AI. Check required models, accounts and compatible hardware with the provider.",
        ("gaming", false) => "Für Spiele und deren Verwaltung. Änderungen an Spielen erfolgen erst in der jeweiligen Anwendung.",
        ("gaming", true) => "For games and their management. Changes to games are made in the respective application.",
        ("tools", false) => "Für die oben beschriebene Aufgabe am PC. Einstellungen werden erst in der jeweiligen Anwendung vorgenommen.",
        ("tools", true) => "For the PC task described above. Settings are changed in the respective application.",
        (_, false) => "Anhand der Herstellerbeschreibung prüfen, ob die Anwendung zum gewünschten Einsatzzweck passt.",
        _ => "Check the publisher description to decide whether this application meets your needs."
    };
}
