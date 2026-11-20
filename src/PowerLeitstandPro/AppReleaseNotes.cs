using System.Text.RegularExpressions;

namespace PowerControlProWpf;

// Save both languages in backups and use the same text for the GitHub release.
internal static class AppReleaseNotes
{
    internal const string German = """
        Neu in Version 1.1.9 – Hotfix 2 zum Testen

        • Die Programmliste zeigt im Microsoft-Store-Filter eigenständige Apps des angemeldeten Kontos. Optionale Windows-Standard-Apps lassen sich separat mit einer Erklärung zur Entfernung prüfen.
        • Gesperrte Deinstallationsaktionen zeigen ihren Grund. Ein vorhandenes Windows-Datum wird gekennzeichnet. Registrierte Ersatzbefehle werden vor der Verwendung geprüft.
        • Unter Einstellungen lässt sich auch eine portable Kopie entfernen. Der Dialog zeigt den Dateipfad und bietet Protokolle, Einstellungen mit Sound-Cache sowie Programmsicherungen einzeln zur Auswahl an.
        """;

    internal const string English = """
        New in version 1.1.9 – Hotfix 2 for testing

        • The Microsoft Store filter lists standalone apps for the signed-in account. Optional Windows default apps have a separate view explaining the effect of removal.
        • Disabled uninstall actions show their reason. The list labels dates supplied by Windows and checks registered alternative commands before use.
        • Settings can remove a portable copy. The dialog shows the executable path and offers separate choices for logs, settings with the sound cache, and program backups.
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
