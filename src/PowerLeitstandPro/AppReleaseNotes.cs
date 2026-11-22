using System.Text.RegularExpressions;

namespace PowerControlProWpf;

// Save both languages in backups and use the same text for the GitHub release.
internal static class AppReleaseNotes
{
    internal const string German = """
        Neu in Version 1.1.9 – Hotfix 6

        • **Portable EXE** Die benötigte .NET-Desktoplaufzeit ist in der EXE enthalten.
        • **Hotfix auswählen** Der Installationsdialog zeigt die Änderungen des gewählten Hotfixes direkt als Kacheln. Über die Auswahl kannst du zwischen verfügbaren Hotfixes wechseln.
        • **Installation bestätigen** Vor dem Download siehst du die installierte und die ausgewählte Fassung sowie die Änderungen. Über „Zurück“ kannst du deine Auswahl anpassen.
        • **Hotfix-Verlauf öffnen** Ein Klick auf die Hotfix-Anzahl klappt die zugehörigen Versionen in der Liste auf oder zu.
        """;

    internal const string English = """
        New in version 1.1.9 – Hotfix 6

        • **Portable EXE** The required .NET desktop runtime is included in the executable.
        • **Choose a hotfix** The installation dialog displays the selected hotfix changes as cards. Use the selector to switch between available hotfixes.
        • **Confirm installation** Review the installed and selected editions and their changes before downloading. Choose “Back” to adjust your selection.
        • **Open hotfix history** Click the hotfix count to expand or collapse the versions in the list.
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
