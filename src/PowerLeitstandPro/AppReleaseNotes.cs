using System.Text.RegularExpressions;

namespace PowerControlProWpf;

// Save both languages in backups and use the same text for the GitHub release.
internal static class AppReleaseNotes
{
    internal const string German = """
        Neu in Version 1.1.6 – Programme und Paketmanager

        • Neue Programme direkt aus dem Detaildialog installieren. Bei der Ersteinrichtung lässt sich eine Desktop-Verknüpfung auswählen.
        • Windows Terminal: Ein bestätigtes Update für die Windows-App wird direkt über WinGet installiert und anschließend anhand der Windows-Paketversion geprüft.
        • PSResourceGet: Eine fehlerhafte Benutzerinstallation der unterstützten Version kann repariert werden. Scheitert die Prüfung, wird die vorherige Kopie wiederhergestellt.
        • Die Installationswarteschlange zeigt fehlende Installer und blockierte Updates mit einem Hinweis an.
        """;

    internal const string English = """
        New in version 1.1.6 – Programs and package managers

        • Install new programs directly from their details. A desktop shortcut can be selected for a first installation.
        • Windows Terminal: Verified updates for the Windows app are installed directly through WinGet and then checked against the Windows package version.
        • PSResourceGet: A broken user installation of the supported version can be repaired. If verification fails, the previous copy is restored.
        • The installation queue explains missing installers and blocked updates.
        """;

    internal const string Current = "## Deutsch\n" + German + "\n\n## English\n" + English;

    internal static string Select(string notes, bool english)
    {
        if (string.IsNullOrWhiteSpace(notes)) return english ? "No release notes stored for this version." : "Für diese Version sind keine Versionshinweise hinterlegt.";
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
        return (english ? "A translation is not available for this release. Original release notes:\n\n"
            : "Für diese Veröffentlichung fehlt eine Übersetzung. Original-Versionshinweise:\n\n") + notes.Trim();
    }
}
