using System.Text.RegularExpressions;

namespace PowerControlProWpf;

// Save both languages in backups and use the same text for the GitHub release.
internal static class AppReleaseNotes
{
    internal const string German = """
        Neu in Version 1.1.8 – Programme vor dem Update schließen

        • Alle Updates im Programmkatalog nutzen dieselbe Prozessprüfung. PowerLeitstand versucht, eindeutig zugeordnete Programme des angemeldeten Benutzers geordnet zu schließen. Die Prüfung berücksichtigt die Dateimerkmale von Node.js, Opera und Opera GX.
        • Bleibt ein Programm offen oder ist seine Zuordnung unklar, nennt die Ergebniszeile den nächsten Schritt. Weitere ausgewählte Updates laufen in der Warteschlange weiter.
        """;

    internal const string English = """
        New in version 1.1.8 – Closing programs before updates

        • All updates in the program catalog use the same process checks. PowerLeitstand tries to close programs gracefully when they are unambiguously linked to the selected installation and signed-in user. The checks recognize the executable metadata of Node.js, Opera, and Opera GX.
        • If a program stays open or its process cannot be matched safely, its result row explains what to do next. Other selected updates continue through the queue.
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
