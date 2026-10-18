# Lokale Testversion v1.1.3 – Update und Schnellzugriff

**Nachtrag 18.10.2026:** Der Nutzer meldete einen Absturz beim Betätigen des Schnellzugriffs. Das Windows-.NET-Protokoll belegt einen erneuten Schließaufruf aus `Deactivated`, während das Menü bereits schließt. Die ursprünglichen Prüfungen mit nicht aktivierten Darstellungsfenstern deckten diesen Ablauf nicht ab. Korrigierte Testversion: v1.1.4; Schließwiedereintritt geschützt und Aktionen nach der Schließverarbeitung eingeplant. Ein gezielter Regressionstest sowie die Verbindung zu echtem Timerabbruch und Zentrieren ergänzen die frühere Prüfung. Die alte Test-EXE v1.1.3 nicht weiter verwenden.

Umsetzung auf Freigabe vom 18.10.2026. Noch kein GitHub-Release.

## Update

Der neue Update-Helfer ersetzt die EXE und wartet auf eine Startbestätigung des exakten neuen Prozesses. Die App meldet sich erst nach dem gerenderten Hauptfenster, zwei Sekunden Wartezeit und einem weiteren Dispatcher-Durchlauf. Ohne Bestätigung innerhalb von 90 Sekunden oder bei vorherigem Prozessende wird der Kandidat beendet und die alte EXE zurückgespielt und neu gestartet. Der Helfer übernimmt die vorhandenen Rechte; beim Wiederherstellen wird kein zusätzlicher automatischer Administrator-Neustart ausgelöst.

Nach erfolgreicher Bestätigung wird das Backup gelöscht. Beim erfolgreichen Zurückspielen wird die Sicherung an den ursprünglichen EXE-Pfad verschoben, sodass ebenfalls keine zusätzliche `.bak` bleibt. Wenn Wiederherstellung oder Löschen blockiert sind, bleibt die Sicherung mit Fehlermeldung erhalten. Bereits vorhandene Sicherungen älterer Updates werden nicht pauschal gelöscht. Die Startkontrolle überwacht den ersten Start, nicht jeden späteren Absturz.

Die neue Überwachung gilt für Updates aus v1.1.3 heraus. Ein Update aus v1.1.2 verwendet noch den dortigen alten Helfer.

## Eine Instanz

Ein Mutex und eine lokale, auf den aktuellen Benutzer beschränkte Pipe koordinieren eine interaktive Instanz je Benutzer und Windows-Sitzung. Eine weitere EXE-Kopie aktiviert das bestehende Fenster und beendet sich. Minimierte und im Infobereich verborgene Fenster werden eingeblendet. Update- und Reparaturhelfer sind ausgenommen. Der Administrator-Neustart wartet auf das Ende der vorherigen Instanz.

## Schnellzugriff

Das freigegebene Menü erscheint per Klick auf das Symbol im Infobereich. Doppelklick öffnet das Hauptfenster. Klicken außerhalb und Escape schließen das Menü. Die Menüfarben, Schriften und vorhandenen Aktionsicons folgen dem gewählten Design. Streifen bleiben innerhalb des Rahmens. Kleine Arbeitsflächen können intern ohne sichtbare Scrollleiste scrollen.

Bei aktivem Timer sind Herunterfahren, Neustart, Abmelden, Sperren und Programm beenden gesperrt. Fenster öffnen, zentrieren und Timer abbrechen bleiben verfügbar. Nach dem tatsächlichen Stoppen des Timers werden die Aktionen wieder freigegeben. Ein erneuter Zustandstest direkt vor der Ausführung verhindert die Umgehung durch veraltete Anzeigezustände. Reparatur und Update sperren Systemaktionen und Beenden ebenfalls. Systemaktionen nutzen die bestehenden Bestätigungen.

## Prüfungen

- 18 Wiederherstellungs-/IPC-Prüfungen mit temporären Dateien und eigenen Prozessen.
- 28 WPF-Menüfälle: sieben Designs × zwei Sprachen × Timer aktiv/inaktiv, dazu Reparaturzustand, Ausführungssperre und internes Scrollen.
- Reale Timer-Abbruchverbindung mit isoliertem Protokollverzeichnis.
- 17 bestehende Downloadprüfungen erneut bestanden.
- Portable EXE: drei zusätzliche Starts aus einer anderen Dateikopie abgefangen, vorhandenes Fenster wiederhergestellt.
- Echter Update-Helfer: neue WPF-EXE gestartet, Rückmeldung empfangen und Sicherung entfernt.
- Echter Update-Helfer: Kandidat ohne Rückmeldung beendet, vorherige EXE wiederhergestellt und reagierend gestartet, Sicherung entfernt.

Keine echten Systemaktionen ausgelöst. Eigener Autostart-Eintrag nach den EXE-Tests wiederhergestellt. Keine zusätzliche DPI-/Mehrmonitorprüfung und kein manueller UAC-Klicktest. Kein abgeschlossener Virenscan für diese neue Testdatei.
