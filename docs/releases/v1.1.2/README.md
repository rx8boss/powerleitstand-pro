# PowerLeitstand PRO v1.1.2 – Prüfnachweise

- [Programm, ZIP und Prüfsummen im Release](https://github.com/rx8boss/powerleitstand-pro/releases/tag/v1.1.2)

Die veröffentlichte EXE gehört zum Quellstand `cbb978e7379bc802c264a4d68ef26c57b0abbbe5`.
Ihr SHA-256 lautet `9ec1b8efe031ac67e91ef1f1197bebafe1842bec57ee188702f700bf7a2f6a78`.
Der SHA-256 des ZIP lautet `da4a4f4d6b6cc8577e7182be14e4de528f033f5921e3bbfa0923be5703098a4c`.

## Prüfung vor der Veröffentlichung

- 17 Downloadtests mit lokalen HTTP-Fixtures bestanden: bekannte/unbekannte Größe, Zwischenfortschritt, Abbruch, falsche Prüfsumme, unvollständiger Download, HTTP-Fehler und Erhalt vorhandener Dateien.
- 70 Dialogprüfungen bestanden: sieben Designs × fünf Größenoptionen × zwei Sprachen. Zentrierung, erreichbare Bedienelemente, Fortschrittsformatierung, Prüfphase und wartender Abbruch geprüft. Zusätzlich X/Escape und Sperre nach Übergabe an den Update-Helfer geprüft.
- Finale portable EXE gestartet; WPF-Fenster reagiert. Startprobe 7,8 Sekunden als Einzelmessung. Der ursprüngliche eigene Autostart-Eintrag wurde wiederhergestellt.
- Hauptbild und Download-Dialog in der README zeigen echte WPF-Aufnahmen mit ausdrücklich gekennzeichneten Beispieldaten.

## Prüfung des veröffentlichten Downloads am 18.10.2026

GitHub meldet v1.1.2 als aktuelle Version mit genau einer EXE. Die SHA-256-Digests der Programmdateien (EXE, ZIP und Prüfsummenliste) wurden mit den lokalen Dateien verglichen.

Ein isolierter Treiber mit dem produktiven Updatecode hat anschließend die veröffentlichte v1.1.2 von GitHub heruntergeladen, ihre SHA-256-Prüfsumme geprüft und die Test-EXE atomar ersetzt. Der Helfer wartete auf das Ende des alten Prozesses. Die reale neue WPF-App wurde neu gestartet, meldete Version `1.1.2+cbb978e7379bc802c264a4d68ef26c57b0abbbe5` und reagierte. Alle drei Fortschrittsphasen Download, Prüfung und Neustart wurden erfasst. Der Autostart-Zustand wurde geprüft und der ursprüngliche eigene Eintrag wiederhergestellt. Die installierte Programm-EXE wurde dabei nicht ersetzt.

## Grenzen

Dialogprüfungen bei 96 DPI; keine zusätzliche DPI-/Mehrmonitorprüfung und kein vollständiger manueller Klicktest aus der alten Programmoberfläche. Die Dialogzustände und der reale Netzwerk-/Austauschablauf wurden getrennt geprüft. Es wurden keine echten Reparatur-, Konto- oder Herunterfahraktionen ausgelöst.

Dieser Abschnitt ergänzt die danach erfolgte Prüfung des öffentlichen Downloads. Für diese EXE liegt kein abgeschlossener Virenscan vor; eine SHA-256-Prüfung ist kein Virenscan. VirusTotal wurde auf Nutzerwunsch nicht angebunden.

Beim Update aus v1.1.1 wird der neue Download-Dialog mit v1.1.2 installiert. Er wird bei späteren Updates aus v1.1.2 genutzt.
