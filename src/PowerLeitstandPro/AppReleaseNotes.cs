using System.Text.RegularExpressions;

namespace PowerControlProWpf;

// Save both languages in backups and use the same text for the GitHub release.
internal static class AppReleaseNotes
{
    internal const string German = """
        Neu in Version 1.1.9 – Hotfix 4

        • **Versionshinweise öffnen** Ein Klick auf eine stabile Version, Beta oder einen Hotfix öffnet ein eigenes Infofenster. Jede Änderung steht in einer Kachel. Die installierte Fassung trägt ein Häkchen.
        • **Frühere Hotfixes ansehen** Unter „Stabil + Hotfixes“ lassen sich die Hotfixes einer stabilen Version aufklappen. Auch der installierte und ältere Hotfixes bleiben dort sichtbar.
        """;

    internal const string English = """
        New in version 1.1.9 – Hotfix 4

        • **Open release notes** Click a stable release, beta, or hotfix to open its own details window. Each change appears in a card. A check mark identifies the installed build.
        • **Browse earlier hotfixes** Under Stable + hotfixes, expand a stable release to see its hotfix history, including installed and older fixes.
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
