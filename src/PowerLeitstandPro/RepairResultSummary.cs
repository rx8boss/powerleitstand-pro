namespace PowerControlProWpf;

internal sealed record RepairResultSummary(
    string Title, string Message, string Detail, bool IsSuccess, bool CanRestart, bool WasCancelled = false, string? Output = null)
{
    internal bool ShowFullOutput { get; init; }

    internal static RepairResultSummary Create(string mode, int? exitCode, bool cancelled,
        string output, string hint, TimeSpan elapsed, bool english)
    {
        var definition = RepairTool.Find(mode);
        var tool = mode switch { "sfc" => "SFC", "dism" => "DISM", _ => definition?.Title(english) ?? mode };
        var repair = definition?.IsRepair == true;
        if (mode == "disk" && exitCode == 1 && !cancelled)
            return new(english ? "File system errors fixed" : "Dateisystemfehler behoben",
                english ? "CHKDSK reported that it fixed errors. See the repair log for details."
                    : "CHKDSK meldet behobene Fehler. Details stehen im Reparaturprotokoll.",
                "CHKDSK · Exit-Code 1", true, false);
        var duration = elapsed.ToString(elapsed.TotalHours >= 1 ? @"hh\:mm\:ss" : @"mm\:ss");
        var detail = english ? $"{tool} · elapsed {duration}" : $"{tool} · Laufzeit {duration}";
        if (cancelled)
            return new(english ? "Operation cancelled" : "Vorgang abgebrochen",
                english ? "The operation did not finish. No result can be confirmed."
                    : "Der Vorgang wurde nicht beendet. Ein Ergebnis kann nicht bestätigt werden.",
                detail, false, false, true, definition?.UsesLocalizedQuery == true ? output.Trim() : null)
            { ShowFullOutput = definition?.UsesLocalizedQuery == true };

        if (definition?.ShowsDiagnosticOutput == true)
        {
            if (exitCode != 0)
                return new(definition.IsNetworkAction ? (english ? "Network action failed" : "Netzwerkaktion fehlgeschlagen")
                        : (english ? "Diagnostic query failed" : "Diagnoseabfrage fehlgeschlagen"),
                    hint, detail + $" · Exit-Code {exitCode?.ToString() ?? "?"}", false, false, Output: output.Trim())
                { ShowFullOutput = definition.UsesLocalizedQuery };

            return new(tool, definition.IsNetworkReset
                    ? (english ? "The reset command finished. Restart Windows to finish applying the changes."
                        : "Der Reset-Befehl ist beendet. Starte Windows neu, damit die Änderungen wirksam werden.")
                    : string.IsNullOrWhiteSpace(output)
                    ? (english ? "Windows returned no matching entries." : "Windows hat keine passenden Einträge gemeldet.")
                    : definition.IsNetworkAction ? (english ? "Windows completed the action." : "Windows hat die Aktion ausgeführt.")
                        : (english ? "Results from this query." : "Ergebnisse dieser Abfrage."),
                detail, true, false, Output: output.Trim())
            { ShowFullOutput = definition.UsesLocalizedQuery };
        }

        if (exitCode != 0)
            return new(english ? "Operation reported an error" : "Fehler beim Vorgang",
                string.IsNullOrWhiteSpace(hint)
                    ? (english ? "See the repair log for details." : "Details stehen im Reparaturprotokoll.")
                    : hint,
                detail + (english ? $" · exit code {exitCode?.ToString() ?? "unknown"}"
                    : $" · Exit-Code {exitCode?.ToString() ?? "unbekannt"}"), false, false);

        if (mode == "sfc")
        {
            if (ContainsAny(output, "nicht reparieren", "nicht repariert", "could not fix", "unable to fix", "could not repair"))
                return new(english ? "Files could not all be repaired" : "Nicht alle Dateien repariert",
                    english ? "Check CBS.log and run DISM before trying SFC again."
                        : "CBS.log prüfen und vor einem erneuten SFC-Lauf DISM ausführen.",
                    detail + " · Exit-Code 0", false, false);
            if (ContainsAny(output, "keine Integritätsverletzungen", "did not find any integrity violations"))
                return new(english ? "No integrity violations found" : "Keine Integritätsverletzungen gefunden",
                    english ? "SFC did not report damaged system files. A restart is optional."
                        : "SFC hat keine beschädigten Systemdateien gemeldet. Ein Neustart ist optional.",
                    detail + " · Exit-Code 0", true, repair);
            if (ContainsAny(output, "erfolgreich repariert", "successfully repaired"))
                return new(english ? "System files repaired" : "Systemdateien repariert",
                    english ? "SFC reported that damaged files were repaired. You can restart now or later."
                        : "SFC hat beschädigte Dateien repariert. Du kannst jetzt oder später neu starten.",
                    detail + " · Exit-Code 0", true, repair);
        }

        return new(english ? $"{tool} completed" : $"{tool} abgeschlossen",
            english ? "The command finished without a reported error. See the repair log for details."
                : "Der Befehl endete ohne gemeldeten Fehler. Details stehen im Reparaturprotokoll.",
            detail + " · Exit-Code 0", true, repair);
    }

    private static bool ContainsAny(string text, params string[] phrases) =>
        phrases.Any(phrase => text.Contains(phrase, StringComparison.OrdinalIgnoreCase));
}
