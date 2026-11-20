using System.Text.RegularExpressions;

namespace PowerControlProWpf;

// Save both languages in backups and use the same text for the GitHub release.
internal static class AppReleaseNotes
{
    internal const string German = """
        Neu in Version 1.1.9 – Programme verwalten und Update-Kanäle auswählen

        • Die Einzelprüfung im Programmkatalog berücksichtigt zusätzliche WinGet-Zuordnungen. Ein passender Updateweg wird nur bei eindeutig bestätigter Paketkennung, Programmbezeichnung und installierter Version angeboten.
        • Portable WinGet-Programme werden erneut anhand ihrer Programmdatei geprüft. Fehlende Dateien und verbliebene WinGet-Einträge werden getrennt angezeigt; die Deinstallation verwendet den zugeordneten Eintrag und Installationsbereich.
        • Desktop-Verknüpfungen berücksichtigen auch portable Programme mit versionsabhängigen EXE-Namen, beispielsweise AdwCleaner. Ziel und Ablage der Verknüpfung werden geprüft.
        • Ein gemeinsamer Deinstallationsdialog zeigt Programm, Speicherort, Fortschritt und Ergebnis. Nach der Deinstallation wird gezielt dieses Programm erneut geprüft.
        • In den Einstellungen kann PowerLeitstand selbst zur Deinstallation vorbereitet werden. Bei bestätigter WinGet-Zuordnung wird die Entfernung übergeben. Einstellungen und Protokolle bleiben standardmäßig erhalten; bei manuellen Kopien lässt sich der Programmordner öffnen.
        • Der Update-Dialog hat eine feste Größe und zeigt bis zu drei vollständige Versionszeilen. Die gespeicherte Auswahl unterscheidet Nur stabil, Stabil + Hotfixes und ausschließlich Beta. Ohne passende Beta bleibt die Versionsliste leer.
        • Die Updateprüfung erkennt nummerierte Hotfixes und Beta-Versionen auf GitHub. ZIP-Pakete werden vor dem Austausch anhand ihrer SHA-256-Prüfsumme und der enthaltenen EXE-Version geprüft.
        • Optionale Updates erscheinen mit einem Zähler an der Glocke und kurzen Hinweisen darunter. Ist die stabile Version aktuell, bleibt die Glocke grün.
        • Es werden bis zu acht geprüfte Programmsicherungen aufbewahrt. Nach erfolgreicher Erstellung einer weiteren Sicherung wird die älteste entfernt.
        • Die deutsche Flagge in der Sprachauswahl wird wieder vollständig angezeigt.
        """;

    internal const string English = """
        New in version 1.1.9 – Program management and update channels

        • Individual catalog checks support additional WinGet mappings. An update route is offered only when the package identifier, program name and installed version have been verified unambiguously.
        • Portable WinGet programs are rechecked against their executable. Missing files and remaining WinGet registrations are shown separately; removal uses the matched registration and installation scope.
        • Desktop shortcuts also support portable programs with versioned executable names, including AdwCleaner. The shortcut target and location are verified.
        • A shared uninstall dialog shows the program, location, progress and result. Only the affected program is rechecked after removal.
        • Settings can prepare removal of PowerLeitstand itself. A verified WinGet registration is handed over for removal. Settings and logs are kept by default; manually extracted copies offer access to their program folder.
        • The update dialog has a fixed size and displays up to three complete version rows. The saved selection offers Stable only, Stable + hotfixes and Beta only. The version list remains empty when no matching beta is available.
        • Update checks recognize numbered hotfixes and beta versions on GitHub. ZIP packages are verified against their SHA-256 checksum and executable version before replacement.
        • Optional updates appear as a counter on the bell with short notices underneath. The bell stays green when the stable version is current.
        • Up to eight verified application backups are retained. Once another backup has been created successfully, the oldest is removed.
        • The German flag in the language selector is fully visible again.
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
