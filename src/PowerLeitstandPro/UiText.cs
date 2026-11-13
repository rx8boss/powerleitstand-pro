namespace PowerControlProWpf;

// Translate only our own messages. Windows/tool output and file paths remain original evidence.
internal static class UiText
{
    private static readonly (string German, string English)[] Messages =
    [
        ("Programmzuordnungen können nicht gelesen werden.", "Program file associations cannot be read."),
        ("Zu viele Programmzuordnungen gespeichert.", "Too many program file associations are stored."),
        ("Bitte eine vorhandene lokale Programm-EXE auswählen.", "Select an existing local program executable."),
        ("Keine unterstützte Windows-Programmdatei.", "Unsupported Windows executable."),
        ("Der Dateikopf der Programmdatei ist ungültig.", "The executable header is invalid."),
        ("Bitte eine Programm-EXE statt einer Bibliothek auswählen.", "Select a program executable, not a library."),
        ("Produkt und Hersteller passen nicht zum gewählten Programm.", "Product and publisher do not match the selected program."),
        ("Bitte die Hauptdatei statt Installer oder Hilfsprogramm auswählen.", "Select the main executable, not a setup or helper."),
        ("Verknüpfte Ablagen für Programmzuordnungen werden nicht verwendet.", "Linked storage locations are not used for program file associations."),
        ("Registrierungssicherung fehlgeschlagen.", "Registry backup failed"),
        ("Die Programmdatei hat sich geändert.", "Installed file changed"),
        ("Die Ventoy-Paketversion wurde nicht bestätigt.", "Ventoy package version not confirmed"),
        ("Die Microsoft-Signatur wurde nicht bestätigt.", "Microsoft signature not confirmed"),
        ("Die Malwarebytes-Signatur wurde nicht bestätigt.", "Malwarebytes signature was not verified."),
        ("Die Tukui-Datei oder Entwicklersignatur wurde nicht bestätigt.", "The Tukui executable or developer signature was not verified."),
        ("Der Tukui-Download ist ungültig.", "The Tukui download is invalid."),
        ("Das portable Tukui-Archiv wurde nicht bestätigt.", "The Tukui standalone archive was not verified."),
        ("Das geprüfte Tukui-Paket ist abgelaufen. Bitte erneut prüfen.", "The verified Tukui package has expired. Please check again."),
        ("Der Speicherort des laufenden Programms ist nicht eindeutig bestätigt. Bitte die Programmdatei zuordnen oder das Programm selbst schließen.", "The running application's location is not uniquely verified. Associate its executable or close the application yourself."),
        ("Das laufende Programm konnte nicht sicher zugeordnet werden. Bitte das Programm selbst schließen.", "The running application could not be safely identified. Please close it yourself."),
        ("Windows konnte das Programm nicht geordnet schließen.", "Windows could not close the application gracefully."),
        ("Das Programm ist noch geöffnet oder konnte nicht geordnet beendet werden. Bitte Arbeit speichern und das Programm schließen; danach erneut versuchen.", "The application is still open or could not be closed gracefully. Save your work and close it, then try again."),
        ("PowerLeitstand kann sich hier nicht selbst schließen. Bitte die eigene Updatefunktion verwenden.", "PowerLeitstand cannot close itself here. Please use its own update feature."),
        ("Der Deinstallationspfad ist ungültig.", "Invalid uninstaller path"),
        ("Die MSI-Produktkennung wurde nicht bestätigt.", "MSI product identity not confirmed"),
        ("Die Auswahl der Programmreste hat sich geändert.", "Remainder selection changed"),
        ("Der Ordner hat sich geändert.", "Directory changed"),
        ("Die Größe des Herstellerdownloads stimmt nicht überein.", "Download size mismatch."),
        ("Der Herstellerdownload ist unvollständig.", "Incomplete download."),
        ("Der Herstellerdownload ist zu groß.", "Download too large."),
        ("Die Prüfsumme des Herstellerdownloads stimmt nicht überein.", "Checksum mismatch."),
        ("Das portable Archiv wurde nicht bestätigt.", "Portable archive layout not verified."),
        ("Die portable Quelle oder Datei hat sich geändert. Bitte erneut prüfen.", "Portable source or file changed. Check again."),
        ("Die Herstellerdatei hat eine andere Produktversion oder Architektur.", "Publisher file has a different product version or architecture."),
        ("Die portable Datei wurde nicht mehr bestätigt.", "Portable file no longer verified."),
        ("Bitte die portable App zuerst schließen.", "Close the portable app first."),
        ("Die Microsoft-Signatur wurde nicht bestätigt.", "Microsoft signature verification failed"),
        ("Eine portable Programmdatei wurde zwischenzeitlich geändert.", "Portable component changed"),
        ("Die WinGet-Paketkennung hat sich geändert.", "WinGet package identity changed"),
        ("Das Programm ist bereits installiert. Bitte den Bestand vor einem Update neu prüfen.", "Program is already installed; refresh the inventory before updating."),
        ("Das installierte WinGet-Ziel wurde nicht mehr bestätigt.", "Installed WinGet target is no longer confirmed."),
        ("Die gewählte WinGet-Version ist nicht mehr verfügbar. Bitte erneut prüfen.", "WinGet target version no longer available. Please check again."),
        ("Der GPU-Z-Herstellerdownload hat sich geändert. Bitte erneut prüfen.", "GPU-Z manufacturer download changed. Please check again."),
        ("Das GPU-Z-Downloadziel wurde nicht bestätigt.", "GPU-Z download endpoint not verified"),
        ("Die GPU-Z-Serverauswahl wurde nicht bestätigt.", "GPU-Z server selection not verified"),
        ("Ungültiger portabler Zielordner.", "Invalid portable destination."),
        ("Ziel ist bereits vorhanden; es wird nichts überschrieben.", "Destination exists; nothing will be overwritten."),
        ("Portable Version hat sich geändert; bitte erneut prüfen.", "Portable release changed; please check again."),
        ("Die Größe des portablen Downloads stimmt nicht überein.", "Portable download size mismatch."),
        ("Der portable Download ist unvollständig.", "Portable download is incomplete."),
        ("Die portable Paketprüfsumme stimmt nicht überein.", "Portable package checksum mismatch."),
        ("Die OBS-Programmdatei fehlt im portablen Paket.", "The OBS executable is missing from the portable package."),
        ("Ungültiges portables Archiv.", "Invalid portable archive."),
        ("Das portable Archiv enthält ungültige Pfade.", "The portable archive contains invalid paths."),
        ("Das portable Archiv ist zu groß.", "The portable archive is too large."),
        ("Die entpackte Dateigröße stimmt nicht überein.", "Extracted file size mismatch."),
        ("Eine portable Datei ist unvollständig.", "A portable file is incomplete."),
        ("Portable Installation nicht bestätigt.", "Portable installation not verified."),
        ("Ungültige Sicherung.", "Invalid backup."),
        ("Unbekannte Sicherung.", "Unrecognized backup."),
        ("Ungültige Sicherungsversion.", "Invalid backup version."),
        ("Sicherung konnte nicht verifiziert werden.", "The backup could not be verified."),
        ("Sicherungsprüfung fehlgeschlagen.", "Backup verification failed."),
        ("Unbekannter Inhalt im Sicherungsordner; nichts wird gelöscht.", "Unknown contents in the backup folder; nothing will be deleted."),
        ("Verknüpfte Sicherungsordner werden nicht verwendet.", "Linked backup folders are not supported."),
        ("Ungültiger Sicherungspfad.", "Invalid backup path."),
        ("Ungültige Sicherungsdatei.", "Invalid backup file."),
        ("Der Autostart-Eintrag hat ein ungültiges Format.", "The startup entry has an invalid format."),
        ("Der neue Autostart-Pfad konnte nicht bestätigt werden.", "The new startup path could not be verified."),
        ("Die Autostart-Einstellung konnte nicht bestätigt werden.", "The startup setting could not be verified."),
        ("Der Pfad der ausführbaren App konnte nicht sicher ermittelt werden.", "The application executable path could not be determined safely."),
        ("Die aktuelle Programmdatei wurde nicht gefunden.", "The current application executable was not found."),
        ("Ein Kennwort ist erforderlich.", "A password is required."),
        ("Ein kennwortloses Konto darf kein Kennwort enthalten.", "A passwordless account must not contain a password."),
        ("Der neue Benutzername ist bereits vergeben.", "The new user name is already in use."),
        ("Beim Entfernen darf kein neues Kennwort angegeben werden.", "Do not enter a new password when removing the password."),
        ("Ein neues Kennwort ist erforderlich.", "A new password is required."),
        ("Kennwortänderung fehlgeschlagen; auch die ursprünglichen Kontoflags konnten nicht wiederhergestellt werden.", "Password change failed; the original account flags could not be restored either."),
        ("Ungültiger Benutzername.", "Invalid user name."),
        ("Das ausgewählte Konto hat sich seit dem Laden geändert.", "The selected account has changed since it was loaded."),
        ("Dieses integrierte Windows-Konto ist geschützt.", "This built-in Windows account is protected."),
        ("Das aktuell verwendete Konto kann hier nicht umbenannt oder gelöscht werden.", "The account currently in use cannot be renamed or deleted here."),
        ("Ungültige Release-Version.", "Invalid release version."),
        ("Im GitHub-Release fehlt die Windows-EXE.", "The GitHub release does not contain a Windows EXE."),
        ("Das GitHub-Release enthält mehrere EXE-Dateien. Ein automatisches Update ist nicht eindeutig.", "The GitHub release contains multiple EXE files. An automatic update would be ambiguous."),
        ("Für die EXE fehlt eine gültige SHA-256-Prüfsumme im GitHub-Release.", "The GitHub release does not provide a valid SHA-256 checksum for the EXE."),
        ("Ungültige Release-Downloadadresse.", "Invalid release download address."),
        ("EXE-Pfad nicht ermittelbar.", "The EXE path could not be determined."),
        ("Updates benötigen die veröffentlichte Windows-EXE.", "Updates require the published Windows EXE."),
        ("Installationsordner nicht ermittelbar.", "The installation folder could not be determined."),
        ("Sicherung ist beschädigt.", "The backup is damaged."),
        ("Wiederherstellungsdatei konnte nicht geprüft werden.", "The restore file could not be verified."),
        ("Dateiversion passt nicht zur ausgewählten Version.", "The file version does not match the selected version."),
        ("Ein Timer oder eine Reparatur wurde während des Downloads gestartet.", "A timer or repair was started during the download."),
        ("Ein Timer oder eine Reparatur verhindert den Neustart.", "A timer or repair is preventing the restart."),
        ("Update-Helfer konnte nicht gestartet werden.", "The update helper could not be started."),
        ("Der Download ist unvollständig. Die bisherige EXE bleibt erhalten.", "The download is incomplete. The existing EXE is preserved."),
        ("SHA-256-Prüfsumme stimmt nicht überein. Das Update wurde verworfen.", "The SHA-256 checksum does not match. The update was discarded."),
        ("Sicherung passt nicht zur bisherigen EXE.", "The backup does not match the existing EXE."),
        ("Neue EXE konnte nicht gestartet werden.", "The new EXE could not be started."),
        ("Geprüfte Sicherung fehlt.", "The verified backup is missing."),
        ("Die neue Version hat keinen erfolgreichen Fensterstart bestätigt.", "The new version did not confirm a successful window startup."),
        ("Der Windows-Befehl konnte nicht gestartet werden.", "The Windows command could not be started."),
        ("Die Administratorgruppen-Mitgliedschaft konnte nicht geprüft werden.", "Administrator group membership could not be verified."),
        ("Windows-Benutzerabfrage hat zu lange gedauert.", "The Windows user query timed out."),
        ("Programmdatei nicht gefunden.", "Application executable not found."),
        ("Die Herstellersignatur konnte nicht bestätigt werden.", "The manufacturer's signature could not be verified."),
        ("Das Herstellerpaket ist nicht freigegeben.", "The manufacturer package is not approved."),
        ("Die Paketprüfsumme stimmt nicht überein.", "The package checksum does not match."),
        ("Der Paketstart konnte nicht bestätigt werden.", "The packaged application launch could not be verified."),
        ("Die Downloadquelle ist nicht freigegeben.", "The download source is not approved."),
        ("Die Seitenantwort ist zu groß.", "The page response is too large."),
        ("Zu viele Downloadweiterleitungen.", "Too many download redirects."),
        ("Die Downloaddatei ist zu groß.", "The download file is too large."),
        ("Der Herstellerdownload ist unvollständig.", "The manufacturer download is incomplete."),
        ("Der Installationspfad ist nicht freigegeben.", "The installer path is not approved."),
        ("Programm konnte nicht gestartet werden.", "The application could not be started."),
        ("Die Updateauswahl oder Lizenzbedingungen haben sich geändert. Bitte erneut suchen.", "The update selection or licence terms have changed. Please check again."),
        ("Windows-Updatesuche fehlgeschlagen.", "Windows update search failed."),
        ("Unvollständige Windows-Updatesuche. Bitte erneut prüfen.", "Incomplete Windows update search. Please check again."),
        ("Windows benötigt zunächst den Abschluss eines anderen Vorgangs oder einen Neustart.", "Finish the other Windows operation or restart first."),
        ("Der Windows-Update-Download war nicht vollständig erfolgreich.", "The Windows update download did not fully succeed."),
        ("Der Windows-Update-Download ist unvollständig.", "The Windows update download is incomplete."),
        ("Windows-Update-Helfer ohne Ergebnis beendet.", "Windows update helper ended without a result."),
        ("Die Administratorabfrage konnte nicht gestartet werden.", "The administrator prompt could not be started."),
        ("Windows konnte das Reparaturprogramm nicht starten.", "Windows could not start the repair tool."),
        ("Reparatur vom Benutzer abgebrochen.", "Repair cancelled by the user."),
        ("CBS.log konnte nicht gelesen werden.", "CBS.log could not be read.")
    ];
    internal static string Translate(string message, bool english)
    {
        if (!english) return message;
        if (message == ProgramScoopGit.Missing) return "Scoop requires Git. A suitable existing Git executable could not be verified. Install Git or check its executable and try again.";
        if (message == ProgramScoopGit.Unusable) return "Git was found, but Scoop could not use it. Check the Git installation and try again.";
        if (message == ProgramScoopGit.PreparationFailed) return "Scoop preparation failed. Check the preceding log entry and try again.";
        foreach (var pair in Messages)
            if (message == pair.German || message.StartsWith(pair.German + "\n", StringComparison.Ordinal) || message.StartsWith(pair.German + "\r", StringComparison.Ordinal))
                return pair.English + message[pair.German.Length..];
        return message;
    }
    internal static string Error(Exception error, bool english) => OnlineConnection.Message(error, english);

    internal static bool SavedEnglish()
    {
        try
        {
            var path = System.IO.Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "Power Control PRO", "settings.json");
            using var settings = System.Text.Json.JsonDocument.Parse(System.IO.File.ReadAllText(path));
            return settings.RootElement.TryGetProperty("Language", out var language) && language.GetString() == "en";
        }
        catch (Exception e) when (e is System.IO.IOException or UnauthorizedAccessException or System.Text.Json.JsonException) { return false; }
    }
}
