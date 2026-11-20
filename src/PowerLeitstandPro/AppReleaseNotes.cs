using System.Text.RegularExpressions;

namespace PowerControlProWpf;

// Save both languages in backups and use the same text for the GitHub release.
internal static class AppReleaseNotes
{
    internal const string German = """
        Neu in Version 1.1.9 – Hotfix 1 zum Testen

        • Installierte Programme erscheinen schrittweise. Die erste Liste benötigt keine vollständige Suche nach Programmdateien und Startmenü-Verknüpfungen. Die ausführliche Prüfung erfolgt beim ausgewählten Programm.
        • Die Filter Alle, Programme und Windows-Apps trennen klassische Programme von Windows-App-Paketen. Suche und Kategorien lassen sich weiterhin kombinieren.
        • Windows-Apps verwenden nach Möglichkeit ihren verständlichen Anzeigenamen. Zusätzliche App-Pakete können anhand ihrer eigenen bestätigten Paketidentität entfernt werden, unabhängig vom Programmkatalog.
        • Vor jeder App-Entfernung werden Benutzerregistrierung, Paketversion und Schutzstatus erneut geprüft. Geschützte Windows-Pakete bleiben gesperrt. Die Deinstallation betrifft nur das aktuelle Windows-Konto.
        """;

    internal const string English = """
        New in version 1.1.9 – Hotfix 1 for testing

        • Installed programs appear progressively. The initial list no longer requires a complete executable and Start menu shortcut search. Detailed checks run for the selected program.
        • All, Programs and Windows apps filters separate desktop programs from Windows app packages. Search and category filters can still be combined.
        • Windows apps use friendly display names when available. Additional app packages can be removed using their own verified package identity, independently of the program catalog.
        • User registration, package version and protection status are rechecked before every app removal. Protected Windows packages remain blocked. Removal affects only the current Windows account.
        """;

    internal const string Current = "## Deutsch\n" + German + "\n\n## English\n" + English;

    internal static string Select(string notes, bool english, bool includeFallbackNotice = true)
    {
        if (string.IsNullOrWhiteSpace(notes)) return !includeFallbackNotice ? ""
            : english ? "No release notes stored for this version." : "Für diese Version sind keine Versionshinweise hinterlegt.";
        var headings = Regex.Matches(notes, @"(?im)^#{1,3}\s*(Deutsch|German|English|Englisch|DE|EN)\s*\r?$");
        foreach (Match heading in headings)
        {
            var language = heading.Groups[1].Value.ToLowerInvariant();
            var isEnglish = language is "english" or "englisch" or "en";
            if (isEnglish != english) continue;
            var next = headings.Cast<Match>().FirstOrDefault(m => m.Index > heading.Index);
            var start = heading.Index + heading.Length;
            var selected = notes[start..(next?.Index ?? notes.Length)].Trim();
            if (selected.Length > 0) return selected;
        }
        if (!includeFallbackNotice) return notes.Trim();
        return (english ? "A translation is not available for this release. Original release notes:\n\n"
            : "Für diese Veröffentlichung fehlt eine Übersetzung. Original-Versionshinweise:\n\n") + notes.Trim();
    }
}
