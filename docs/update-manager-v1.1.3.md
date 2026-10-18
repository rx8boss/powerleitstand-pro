# Updateverwaltung und Programmsicherungen – lokale Version 1.1.3

Auf ausdrücklichen Nutzerwunsch bleibt die Produktversion 1.1.3. Die zwischenzeitlichen lokalen Teststände 1.1.4 und 1.1.5 wurden nicht auf GitHub veröffentlicht. Updates, Versionshinweise und Wiederherstellung sind drei Ansichten desselben kompakten WPF-Fensters.

## Bedienung

Optionen → **Updates …** öffnet die kompakte Übersicht mit richtigem Blitz als skalierbarer Grafik. Zielgröße: 620 × 480 Windows-Einheiten, bei Bedarf kleiner als Hauptfenster und verfügbare Bildschirmfläche. Die kompatiblen regulären Veröffentlichungen stehen in einer scrollbaren Liste. **Änderungen anzeigen …** öffnet die vollständigen zugehörigen GitHub-Versionshinweise innerhalb desselben Fensters. Zurück erhält die Auswahl. Die Auswahl einer älteren Version wird mit Hinweis und Bestätigung angeboten. Eine identische veröffentlichte Versionsnummer wird als bereits installiert behandelt. Das Downloadfenster zeigt den echten Fortschritt und die Dateiprüfung.

**Wiederherstellen …** wechselt im selben Fenster zu lokalen Sicherungen. Jeder Eintrag zeigt Version und Zeitpunkt. Vor dem Freigeben der Wiederherstellung wird SHA-256 geprüft. **Sicherung löschen …** fragt nach Bestätigung und entfernt ausschließlich den ausgewählten Sicherungsordner. **Zurück zu Updates** wechselt zur Versionsliste.

Die Hinweise umbrechen innerhalb der verfügbaren Breite. Die Scrollleiste bleibt verborgen. Richtungspfeile zeigen an, ob oberhalb oder unterhalb weitere Hinweise stehen. Bei kleinen Fenstern werden Aktionen untereinander angeordnet. Die Versionsliste passt ihre Höhe an den verfügbaren Platz an; die unteren Hauptbuttons bleiben erreichbar. Der Zurückbutton ist blau, Schließen und Sicherung löschen bleiben rot.

## Sicherungen

Speicherort: `%LOCALAPPDATA%\Power Control PRO\Backups`.

Vor jedem Austausch der EXE wird die bisherige Datei mit Versionsnummer, Zeitstempel, kompakter Änderungsliste und Prüfsumme gesichert. Unterordnernamen beginnen mit `PowerLeitstand-PRO_`, gefolgt von Version und Datum/Uhrzeit sowie einer eindeutigen Kennung. Eine neue Sicherung wird vollständig kopiert und geprüft, bevor ältere Einträge entfernt werden. Es bleiben maximal vier bekannte Sicherungen. Fremde Dateien und Ordnerverknüpfungen werden beim Löschen nicht verfolgt.

Eine Wiederherstellung legt zuvor ebenfalls eine Sicherung der aktuellen EXE an. Bei fehlgeschlagenem Start fällt der Helfer auf die zuvor gesicherte Datei zurück. Die Sicherung verbleibt in AppData. Es entsteht keine zusätzliche `.bak` neben der normalen EXE. Einstellungen und Protokolle werden nicht zurückgesetzt. Bereits vorhandene unbekannte `.bak`-Dateien älterer Updates werden nicht pauschal gelöscht.

Ab 1.1.3 bestätigt die gestartete App ihr gerendertes Fenster über einen privaten lokalen Kommunikationskanal. Für 1.1.1 und 1.1.2 wird stattdessen geprüft, ob ein reagierendes Fenster erscheint und bestehen bleibt. Diese früheren Versionen enthalten nicht alle aktuellen Funktionen; das wird vor einer Rückstufung erklärt. Die früheste ausdrücklich geprüfte Download-Vertragsversion ist 1.1.1; Entwürfe, Vorabversionen, mehrdeutige EXE-Auswahl und fehlende Hashes werden ausgeschlossen.

## Versionshinweise bei jeder Veröffentlichung

1. Produktversion im Projekt setzen.
2. `AppReleaseNotes.Current` mit kurzen verständlichen Änderungen dieser Version aktualisieren.
3. Dieselben Änderungen in die GitHub-Releasebeschreibung übernehmen. Der Dialog lädt diese Beschreibung zur ausgewählten Version automatisch; vorhandene Hinweise werden nicht künstlich gekürzt.
4. Programm bauen und den Update-/Wiederherstellungsvertrag prüfen, bevor der Release freigegeben wird.

Die App erfindet keine Änderungen aus Quellcodeunterschieden. Ohne hinterlegte Hinweise zeigt sie den fehlenden Inhalt ausdrücklich an.

## Prüfung

Nach der Freigabe der Kopfkorrektur: Zurück und Schließen sind 28 × 28 mit Vektorgrafiken. Das X wird beim Hover weiß auf rotem Hintergrund mit rotem Leuchten. Ein eigener `WindowChrome` entfernt den nativen oberen Streifen und erhält die Größenänderung. Klicks auf Pfade innerhalb eines Kopfbuttons starten kein Fensterziehen.

584 Prüfungen im aktuellen Rahmenstand bestanden, davon zusätzliche native Prüfungen für den fehlenden oberen Nicht-Client-Rand und den weiterhin aktiven linken Größenänderungsrand in allen 28 Kombinationen. Aufnahmen mit `PrintWindow` erfassen jetzt das vollständige native Fenster, ergänzend zu den WPF-Inhaltsbildern. Nachweise: `I:/Codex/PowerLeitstand-Tests/20261018-UpdateManager-Rahmen`.

Aktueller kompakter Stand: 472 Prüfungen der Sicherungsverwaltung und 28 Kombinationen aus Design, Sprache und Fenstergröße für alle drei Ansichten. Darunter beschädigte Sicherungen, Vierergrenze, sichere Ordnerlöschung, Bestätigen/Abbrechen, Auswahlbeibehaltung beim Zurückgehen, kleinere Abmessungen als das Hauptfenster und lokaler Offlinebetrieb. Der im Test aufgetretene Zugriffskonflikt zwischen Listenabfrage und Löschen wurde durch lesende Dateiöffnungen mit Löschfreigabe behoben. Beim vorherigen Stand bestanden zusätzlich 17 Downloadprüfungen; der Downloadcode wurde bei der kompakten Gestaltung nicht verändert.

Zusätzlich wurde in einer isolierten EXE-Kopie ein echter Wechsel auf 1.1.2 und die Rückkehr zur neuen 1.1.3 aus einer AppData-Sicherung geprüft. Die öffentliche GitHub-Katalogfunktion wurde mit realen Releases und Versionshinweisen geprüft. Eigener vorheriger Autostart-Eintrag und Test-Sicherungsbestand wurden danach wiederhergestellt. Kein neuer GitHub-Release, keine Änderung der installierten EXE und keine echten Systemaktionen.

Grenzen: 96 DPI; keine zusätzliche DPI-/Mehrmonitorprüfung, kein physischer Gesamtklicktest aus einer alten installierten Oberfläche und kein abgeschlossener Virenscan.
