using System.Text.RegularExpressions;

namespace PowerControlProWpf;

// Save both languages in backups and use the same text for the GitHub release.
internal static class AppReleaseNotes
{
    internal const string German = """
        Neu in Version 1.1.9 – Hotfix 7

        • **Timergröße** Die Schrift der Timerknöpfe bleibt bei automatischer und fester Fenstergröße lesbar.
        • **GPU-Temperatur** Grafikkarten und verfügbare Temperatursensoren von NVIDIA, AMD und Intel werden automatisch erkannt.
        • **Portable EXE** Die benötigte .NET-Desktoplaufzeit ist enthalten.
        """;

    internal const string English = """
        New in version 1.1.9 – Hotfix 7

        • **Timer size** Timer button text remains readable at automatic and fixed window sizes.
        • **GPU temperature** Graphics adapters and available NVIDIA, AMD and Intel temperature sensors are detected automatically.
        • **Portable EXE** The required .NET desktop runtime is included.
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
