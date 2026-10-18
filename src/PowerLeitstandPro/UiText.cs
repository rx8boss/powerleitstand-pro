namespace PowerControlProWpf;

// Translate only our own messages. Windows/tool output and file paths remain original evidence.
internal static class UiText
{
    private static readonly (string German, string English)[] Messages =
    [
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
        ("Die Administratorabfrage konnte nicht gestartet werden.", "The administrator prompt could not be started."),
        ("Windows konnte das Reparaturprogramm nicht starten.", "Windows could not start the repair tool."),
        ("Reparatur vom Benutzer abgebrochen.", "Repair cancelled by the user."),
        ("CBS.log konnte nicht gelesen werden.", "CBS.log could not be read.")
    ];
    internal static string Translate(string message, bool english)
    {
        if (!english) return message;
        foreach (var pair in Messages)
            if (message == pair.German || message.StartsWith(pair.German + "\n", StringComparison.Ordinal) || message.StartsWith(pair.German + "\r", StringComparison.Ordinal))
                return pair.English + message[pair.German.Length..];
        return message;
    }
    internal static string Error(Exception error, bool english) => Translate(error.Message, english);

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
