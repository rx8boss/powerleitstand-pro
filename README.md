# PowerLeitstand PRO · Windows (WPF)

![PowerLeitstand PRO v1.1.4 – Hauptfenster](docs/screenshots/main-window.png)


[![CodeQL · C#](https://github.com/rx8boss/powerleitstand-pro/actions/workflows/codeql.yml/badge.svg)](https://github.com/rx8boss/powerleitstand-pro/actions/workflows/codeql.yml)

[Deutsch](#deutsch) · [English](#english) · [Programm-Vorschau](#vorschau--preview)

## Deutsch

PowerLeitstand PRO – **Windows steuern · Aktionen planen · System reparieren** – ist eine Windows-Desktop-App für sofortige Systemaktionen, zeitgesteuertes Herunterfahren, Neustarten und Sperren sowie die Prüfung und Reparatur von Windows-Systemdateien. Die **WPF-App** ist die Hauptversion; die frühere Electron-Version liegt im Branch [`archive/electron-2026-10-12`](https://github.com/rx8boss/powerleitstand-pro/tree/archive/electron-2026-10-12).

Das Projekt wurde mit Unterstützung künstlicher Intelligenz, unter anderem OpenAI Codex, erstellt und weiterentwickelt.

### Download und Start

[**Windows-EXE herunterladen**](https://github.com/rx8boss/powerleitstand-pro/releases/latest) – ab Version 1.1.0 heißt die Datei `PowerLeitstand-PRO.exe`. Das ZIP enthält ausschließlich die EXE, die zweisprachige Kurzanleitung, den deutschen/englischen Changelog und den Ordner `Lizenzen`. Prüfsummen werden separat angeboten. Die EXE ist portabel und benötigt keine separate .NET-Installation. Prüfe Release-Version, Herkunft und SHA-256-Prüfsumme vor dem Start.

### Funktionen

- Sofort: Herunterfahren, Neustart, Abmelden und Windows sperren – mit passenden Bestätigungen.
- Timer: Herunterfahren, Neustart oder Sperren nach einstellbarer Zeit; laufenden Timer abbrechen und Warnton anpassen.
- Benutzerübersicht mit Erstellen, Umbenennen, Bearbeiten und Löschen lokaler Konten (Name, Beschreibung, Aktivierung, Administratorrolle) sowie Kennwortzurücksetzung oder -entfernung. Ein Konto ohne Kennwort ist beim Erstellen und Zurücksetzen wählbar; Windows-Richtlinien können das ablehnen. Eine Windows-Hello-PIN wird dadurch nicht entfernt; der Dialog verweist für die PIN auf die Anmeldeoptionen des angemeldeten Benutzers. Der Profilordner wird beim Umbenennen nicht umbenannt und beim Löschen des Kontos nicht gelöscht. Änderungen erfordern Administratorrechte; integrierte Konten, das aktuelle Konto und das letzte aktive Administratorkonto sind bei kritischen Aktionen geschützt.
- Windows-Reparatur: DISM und SFC mit Fortschritt, Abbruch, Ergebniszusammenfassung und kopierbarem Befehl. Reparaturen benötigen Administratorrechte und starten den PC nicht automatisch neu.
- Deutsch und Englisch, sieben Designs, vier App-Größen, Tray-Menü und optionaler Autostart.
- Manuelle Update-Prüfung unter Optionen: GitHub-Release-Version prüfen, die einzige EXE im Release anhand des GitHub-SHA-256-Digests verifizieren und nach Zustimmung am bisherigen Ort austauschen. Laufende Timer oder Reparaturen blockieren das Update. Bei keiner oder mehreren EXEs wird nicht automatisch aktualisiert.
- Kompakte Update- und Wiederherstellungsansicht mit Versionsauswahl, zweisprachigen Änderungen und bis zu vier geprüften Programmsicherungen in AppData. Einstellungen und Protokolle bleiben erhalten.
- Schnellzugriff mit Timeranzeige, Timerabbruch, Fenster öffnen und Zentrieren; Schutz vor Mehrfachstarts.
- Dauerhafte Ereignis- und Reparaturprotokolle unter `%LOCALAPPDATA%\Power Control PRO\logs`; Einstellungen unter `%LOCALAPPDATA%\Power Control PRO\settings.json`.

**Wichtig:** Herunterfahren, Neustart, Abmelden und Sperren wirken auf den aktuellen PC. Vor einer Reparatur wichtige Daten sichern. DISM/SFC können länger dauern; ein Abbruch kann einen unvollständigen Reparaturzustand hinterlassen.

### Aus dem Quellcode bauen

Voraussetzungen: Windows x64 und .NET 10 SDK. Im Repository-Verzeichnis:

```powershell
dotnet build .\src\PowerLeitstandPro\PowerControlProWpf.csproj -c Release
dotnet run --project .\tests\ActionSmoke\ActionSmoke.csproj -c Release
dotnet publish .\src\PowerLeitstandPro\PowerControlProWpf.csproj -c Release -r win-x64 --self-contained true -p:PublishSingleFile=true -p:IncludeNativeLibrariesForSelfExtract=true -p:DebugType=None -p:DebugSymbols=false -o .\publish\Windows-x64
```

Die Datei `PowerLeitstand-PRO.exe` liegt danach in `publish\Windows-x64`; dieser Build-Befehl lädt nichts hoch. Generierte Dateien in `bin`, `obj` und `publish` werden nicht versioniert. Icons und Warnsounds sind Projektressourcen. Einstellungen und Logs behalten aus Kompatibilitätsgründen ihren bisherigen `%LOCALAPPDATA%\Power Control PRO`-Pfad.

## English

PowerLeitstand PRO — **Control Windows · Schedule actions · Repair the system** — is a Windows desktop app for immediate system actions, scheduled shutdown, restart and lock, and Windows system-file checks and repair. The **WPF app** is the primary version. The earlier Electron version is preserved in the [`archive/electron-2026-10-12`](https://github.com/rx8boss/powerleitstand-pro/tree/archive/electron-2026-10-12) branch.

This project was created and further developed with AI assistance, including OpenAI Codex.

### Download and run

[**Download the Windows EXE**](https://github.com/rx8boss/powerleitstand-pro/releases/latest). From version 1.1.0 onward, the file is named `PowerLeitstand-PRO.exe`. The release also includes a ZIP with the EXE, a German/English quick-start guide, and its checksum. The portable EXE needs no separate .NET installation. Verify the release version, origin, and SHA-256 checksum before running it.

### Features

- Immediate shutdown, restart, sign-out and Windows lock with appropriate confirmation dialogs.
- Scheduled shutdown, restart or lock; cancel an active timer and customize warning sounds.
- Refreshable local-user overview; create, rename, edit and delete local accounts (name, description, enabled state and administrator role), or reset or remove a password. Passwordless creation and reset are optional, subject to Windows policy. This does not remove a Windows Hello PIN; the dialog links the signed-in user to Windows sign-in options. Renaming does not rename the profile folder; account deletion does not delete it. Administrator rights are required; built-in accounts, the current account and the last active administrator are protected for critical actions.
- DISM and SFC repair with progress, cancellation, result summary and a copyable command. Administrator rights are required; the app does not restart Windows automatically after repair.
- German and English, seven themes, four app sizes, tray menu and optional startup with Windows.
- Manual update check in Options: compare the GitHub release version, verify the release's only EXE against its GitHub SHA-256 digest, and replace the EXE at its current location with consent. Active timers or repairs defer the update. No automatic update occurs if the release has zero or multiple EXEs.
- Compact update and recovery views with version selection, bilingual release notes and up to four verified application backups in AppData. Settings and logs are preserved.
- Quick access with timer status, cancellation, open and center window; single-instance protection.
- Persistent event and repair logs in `%LOCALAPPDATA%\Power Control PRO\logs`; settings in `%LOCALAPPDATA%\Power Control PRO\settings.json`.

**Caution:** Power actions affect the current PC. Back up important files before repair. DISM/SFC can take time, and cancelling may leave the repair incomplete.

### Build from source

Requirements: Windows x64 and the .NET 10 SDK. Run these commands from the repository root:

```powershell
dotnet build .\src\PowerLeitstandPro\PowerControlProWpf.csproj -c Release
dotnet run --project .\tests\ActionSmoke\ActionSmoke.csproj -c Release
dotnet publish .\src\PowerLeitstandPro\PowerControlProWpf.csproj -c Release -r win-x64 --self-contained true -p:PublishSingleFile=true -p:IncludeNativeLibrariesForSelfExtract=true -p:DebugType=None -p:DebugSymbols=false -o .\publish\Windows-x64
```

The standalone `PowerLeitstand-PRO.exe` is placed in `publish\Windows-x64`; these commands do not upload it. Generated `bin`, `obj` and `publish` files are excluded from Git; icons and warning sounds are embedded. Existing settings and logs keep the legacy `%LOCALAPPDATA%\Power Control PRO` path for compatibility.

## Vorschau / Preview

Aktuelle WPF-Oberfläche in Version 1.1.4 (Standarddesign; Diagnose-Platzhalter im Bild oben). Korrekturversion auf Basis von 1.1.3 mit überarbeiteter Installationsbestätigung und passenden Benutzer-Buttons. / Current WPF interface in version 1.1.4 (default theme; diagnostic placeholders in the image above). Correction release based on 1.1.3 with revised installation confirmation and correctly sized user buttons.


### Update-Download / Update download

Sichtbarer Download mit Fortschritt, Datenmenge, Dateiprüfung und Abbruch. Der folgende echte WPF-Dialog zeigt Beispieldaten. / Visible download progress, transferred data, file verification and cancellation. This real WPF dialog shows sample data.

![PowerLeitstand PRO – Update-Download](docs/screenshots/update-dialog.png)

### Versions- und Sprachprüfung / Version and language checks

[Release-Ablauf / Release checklist](docs/release-checklist.md) · [Änderungen 1.1.4 / Changes in 1.1.4](docs/releases/v1.1.4.md)
