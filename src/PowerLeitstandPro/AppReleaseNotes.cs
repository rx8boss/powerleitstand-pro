using System.Text.RegularExpressions;

namespace PowerControlProWpf;

// Save both languages in backups and use the same text for the GitHub release.
internal static class AppReleaseNotes
{
    internal const string German = """
        Neu in Version 1.1.7 – App-Updates und Programminstallation

        • Ein Klick auf die Glocke sucht nach einem App-Update. Ist PowerLeitstand aktuell, erscheint kurz ein Hinweis unter der Glocke. Ist ein Update verfügbar, öffnet sich der Update-Dialog.
        • Die Programminstallation zeigt an, ob die angeforderte Desktop-Verknüpfung erstellt wurde. Für AdwCleaner wird die ausführbare Datei auch im WinGet-Paketordner gesucht. Im Detaildialog installierter Programme lässt sich eine Verknüpfung nachträglich erstellen, wenn das Programm eindeutig zugeordnet werden kann.
        • Bei einer Sammelinstallation teilen sich geeignete neue WinGet-Programme, die systemweit installiert werden, eine Windows-Adminabfrage.
        • Die Schließen- und Abbruchsymbole in Dialogen reagieren einheitlicher, wenn der Mauszeiger darüberliegt.
        """;

    internal const string English = """
        New in version 1.1.7 – App updates and program installation

        • Clicking the bell checks for an app update. If PowerLeitstand is up to date, a brief message appears below the bell. If an update is available, the update dialog opens.
        • Program installation reports whether the requested desktop shortcut was created. AdwCleaner’s executable is also looked up in the WinGet package folder. A shortcut can be added from the details of an installed program when its executable can be identified unambiguously.
        • In a batch installation, eligible new WinGet programs installed for all users share one Windows administrator prompt.
        • Close and cancel icons in dialogs respond more consistently on hover.
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
