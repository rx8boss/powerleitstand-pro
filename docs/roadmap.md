## Abnahme und Release-Vorbereitung – 18.10.2026

- [x] Protokollansichten vom Nutzer abgehakt.
- [x] Neon beim Verschieben vom Nutzer abgehakt.
- [x] Startgeschwindigkeit vom Nutzer abgehakt; keine weitere Optimierung beauftragt.
- [ ] Version 1.1.1: alle Designs/Größen prüfen, Test-EXE und Testbericht erstellen, GitHub-Update testen und veröffentlichen (freigegeben).

# PowerLeitstand PRO – Planungsstand

Dieser Plan beschreibt die nächsten Arbeiten. Die Umsetzung an App und EXE beginnt erst nach Freigabe. Nach jedem Designabschnitt wird ein Screenshot der echten App gezeigt und der nächste Abschnitt erst nach Rückmeldung begonnen. Vorhandene Funktionen bleiben erhalten.

## Aktuelle Prioritäten – Nutzerabnahme vom 18.10.2026

Diese Liste ersetzt die ältere Reihenfolge weiter unten. Abgehakte Punkte beruhen auf der Rückmeldung des Nutzers; sie sind keine zusätzlichen Laufzeittests des Agenten.

- [x] Punkt 1: Log-, DISM-/SFC- und Reparatur-Abbruchdialoge vom Nutzer abgenommen.
- [x] Punkt 3: Größen- und Designcheck vom Nutzer abgehakt.
- [x] Punkt 4: Offene Hardwaretests vom Nutzer abgehakt.
- [ ] Punkt 5, jetzt zuerst: Ruckeln beim Verschieben des Fensters untersuchen. Laut Nutzer vor allem bei Neon. Animierte Leuchteffekte, Zeichenlast und Aktualisierungen der Oberfläche zunächst vergleichen und messen. Erst danach gezielt optimieren; Timer, Reparaturen und andere Funktionen unverändert weiterlaufen lassen. Die Startgeschwindigkeit anschließend getrennt untersuchen.
- [ ] Punkt 2, zurückgestellt: Autostart bei echter Windows-Anmeldung und vollständigen Update-Ablauf nach dem Performance-Abschnitt prüfen.
- [ ] Punkt 6, später: Seite 4 „Windows-Updates“ anhand der vorhandenen Originalvorlage planen und vor Umsetzung freigeben lassen.

### Nächster Umsetzungsvorschlag: Neon beim Verschieben flüssiger machen

**Umsetzungsstand 18.10.2026:** Neonrahmen behalten ihre Zeichenbefehle; Pulse ändern nur deren Deckkraft. Während des Ziehens über die Titelleiste pausieren die verwalteten Neonanimationen und laufen nach dem Loslassen weiter. Diagnose-Sampling läuft weiter, sichtbare Diagnosewerte werden danach nachgezogen. Das Speichern der Fensterposition erfolgt nach dem Ziehen. Timer und Reparaturzustand werden nicht pausiert. Acht reale WPF-Fenster (Standard und drei Neon-Designs, jeweils Kompakt/Groß) mit 24 simulierten Bewegungszustandswechseln geprüft. Drei feste Rahmenhelligkeiten ohne Pixelabweichung zum Ausgangszustand verglichen. Ein echter Maus-/Monitorwechseltest durch den Nutzer steht noch aus; die Startgeschwindigkeit ist ein separater nächster Abschnitt.

1. Standard und Neon bei gleichem Fenstermaß vergleichen; Zeichenzeit und Reaktion der Oberfläche beim Verschieben erfassen.
2. Prüfen, welche animierten Schatten und wiederholten Oberflächenaktualisierungen die Last verursachen. Live-Messwerte werden bereits im Hintergrund erfasst; daraus allein folgt noch keine Diagnose.
3. Gemessene Engpässe beheben. Falls nötig, aufwendige rein visuelle Neon-Aktualisierungen während des Ziehens begrenzen und nach dem Loslassen fortsetzen. Ein solcher Eingriff darf keine Timer oder Reparaturprozesse pausieren.
4. Erscheinungsbild im Ruhezustand sowie Verschieben innerhalb eines Monitors und beim Monitorwechsel prüfen. Erststartgeschwindigkeit separat bewerten.
5. Nach Freigabe der Umsetzung eine neue Test-EXE mit Prüfnachweisen bereitstellen.

## Empfohlene Fenstergrößen (älterer Planungsabschnitt)

Die Werte sind WPF-Fenstermaße in geräteunabhängigen Pixeln (DIP), keine Mindestauflösung des Bildschirms. Vier feste Größen reichen aus; zusätzliche Stufen würden die Layoutprüfung unnötig vervielfachen.

| Auswahl in den Optionen | Geplantes Fenstermaß | Zweck und Entscheidung |
| --- | --- | --- |
| Automatisch | Größte vollständig passende Stufe bis 1200 × 760 | Steht in der Liste ganz oben. Passt sich an den nutzbaren Bereich des aktuellen Bildschirms an. Reicht selbst die kleinste Stufe nicht hinein, wird das Fenster begrenzt und der Inhalt scrollbar gehalten. |
| Kompakt | 860 × 620 | Kleinste feste Stufe; besonders wichtig für die vier Sofort-Buttons und den unteren Statusbereich. Bisher „Klein“; Maß beibehalten. |
| **Standard** | **900 × 650** | **Feste Standardgröße beim ersten Start.** Bisher „Kompakt“; Maß beibehalten. |
| Erweitert | **1050 × 700 vorgeschlagen** | Mehr Höhe für Timer, drei Log-Zeilen und Fußzeile. Bisher „Groß“ mit 1050 × 670. Die zusätzlichen 30 DIP erst anhand echter Screenshots und verfügbarer Bildschirmhöhe bestätigen. |
| Groß | 1200 × 760 | Großes Referenzlayout. Bisher „Sehr groß“; Maß beibehalten. |

Beim ersten Start sind das Design **Standard** und die feste Fenstergröße **Standard (900 × 650)** ausgewählt. Design und Fenstergröße sind getrennte Einstellungen. „Automatisch“ ist lediglich der erste Listeneintrag und wird nur durch Auswahl aktiv. Danach bleibt die vom Nutzer gewählte Größe gespeichert. Bestehende Einstellungen dürfen bei der Änderung der Listenreihenfolge und der Namen nicht unbeabsichtigt auf eine andere Größe zeigen: die bisherigen Indexwerte werden auf stabile Größenkennungen migriert. Feste Größen sollen auf kleineren Bildschirmen erreichbar bleiben; Bildschirmarbeitsfläche, Skalierung und Fensterposition werden geprüft.

## Reihenfolge der Umsetzung nach Freigabe

1. **Ausgangszustand und Vorlage abgleichen.** Echte Screenshots von Startseite, Benutzerübersicht, Windows-Reparatur und Optionen in allen vier festen Größen und „Automatisch“ aufnehmen. Mit der Hauptvorlage vergleichen; Standard und weitere Themes sowie Deutsch/Englisch auf Abstände, Farben, Überlauf und Clipping prüfen. Den konkreten ersten Designabschnitt als Bildentwurf zeigen.
2. **Karten und Reiter gestalten.** Gefüllte Dreiecke oben an Sofort und Timer sowie diagonale Kartenecken auf den anderen Reitern ersetzen. Vorschlag: kurzer, dünner Cyan-Akzent an der oberen Kartenkante mit weichem Verlauf, ohne dreieckige Fläche. Alle drei Reiter einschließlich Rundungen, Zwischenräumen und Kartenhöhen mit der Vorlage abgleichen.
3. **Sofort und Timer einpassen.** Die vier Sofort-Aktionen Herunterfahren, Neustart, Abmelden und Sperren in der kleinsten Größe innerhalb ihrer Buttons zentrieren. Ein begrenztes Innenraster für Icon und Text verwenden; Beschriftungen bei Bedarf sauber umbrechen. Timer-Zeitauswahl und transparente Steuerelemente mit der Vorlage vergleichen. Bestehende Aktionen und Timer-Abbruch erhalten.
4. **Status und Log PRO aufbauen.** Den Status kompakt halten. Mindestens drei *vorhandene* Log-Einträge vollständig sichtbar machen; bei weniger Einträgen keine Ereignisse erfinden. Zeit und Meldung in schmalen Fenstern lesbar halten und die vollständige Meldung als Tooltip anbieten. Live-Diagnosewerte für CPU, RAM, Datenträger und Netzwerk vorerst nicht anschließen; bis dahin nur Symbole und „—“ zeigen.
5. **Log-Bedienung und Fußzeile abstimmen.** Das Drei-Punkte-Menü durch drei kleine, gleich breite, mitskalierende Icon-Buttons direkt unter „Log PRO“ ersetzen: Aktualisieren, Löschen und Ordner öffnen. Löschbestätigung und bestehende Aktionen erhalten, Tooltips und Tastaturbedienung ergänzen. GitHub-Link links, Produkt/Version mittig sowie Update-Status/Logo rechts mit den drei Log-Zeilen kollisionsfrei ausrichten.
6. **Größenoptionen umstellen.** „Automatisch“ nach oben setzen, darunter Kompakt, Standard, Erweitert und Groß. Erststart auf Design Standard und Fenstergröße Standard (900 × 650) setzen; gespeicherte Größen über stabile Kennungen übernehmen. Das vorgeschlagene Maß 1050 × 700 nur verwenden, wenn Bildvergleich und Bildschirmtests es bestätigen. Automatik, manuelle Auswahl, Neustart, Monitorwechsel, Windows-Skalierung und beide Sprachen prüfen.
7. **Abnahme und EXE.** Nach jedem Abschnitt den Screenshot der echten App zeigen und Rückmeldung abwarten. Danach alle drei Reiter, Optionen, Log-Aktionen und Größen gegen die Vorlage prüfen, die portable EXE bauen und ihren Start testen.

## Späterer Funktionsabschnitt

- [x] Live-Diagnose für CPU, RAM, Datenträger und Netzwerk mit echten Messwerten eingebaut (17.10.2026). Die Statusleiste zeigt CPU-/RAM-Auslastung, Aktivität des Windows-Laufwerks und Netzwerkverkehr. Die freigegebenen Hover-Felder zeigen Laufwerke mit Speicherbelegung sowie aktive LAN-/WLAN-Verbindungen mit getrennten Empfangs-/Sendewerten. Farben und Streifen folgen dem jeweiligen Design.
- [x] PC-Info über Windows 10/11, automatische CPU-Topologie und Auslastung je logischem Prozessor ergänzt (17.10.2026). Laufwerke öffnen per Klick im Explorer. Keine 15er-Grenze: Windows-Laufwerksbuchstaben einschließlich USB/Netzlaufwerken und lokale Ordner-Einhängepunkte werden erfasst. Scrollleisten der Diagnosefelder sind unsichtbar, Scrollen bleibt möglich. 30 Laufwerke und 64 logische Prozessoren mit Testdaten in sieben Designs und vier Größen geprüft.
- [x] GPU-Anzeige und Infofeld gemäß freigegebener Vorschau ergänzt (17.10.2026): unten Auslastung und Temperatur, beim Darüberfahren Modell und eigener Grafikspeicher mit Belegung. Kleine/Standardfenster zweizeilig, große Fenster einzeilig. NVIDIA-Treiberabfrage läuft getrennt im Hintergrund mit Zeitbegrenzung; fehlende Werte werden nicht erfunden. Auf GTX 970 live geprüft. AMD-/Intel-Messwerte noch nicht angebunden.
- [x] RAM-Infofeld über BIOS/SMBIOS eingebaut: vier echte DDR3-Module erkannt (8/4/8/4 GB), Teilenummern soweit gemeldet, Steckplätze und BIOS-Geschwindigkeit. In allen Größen zweizeilige Statusleiste mit ausreichender Höhe.
- [x] CPU-Temperatur am 17.10.2026 auf Nutzerwunsch vollständig entfernt: keine Sensorprogramm-Suche, keine CPU-Sensorabfrage, keine Programmlinks oder Warnsymbole. CPU-Auslastung und Kernübersicht bleiben erhalten.
- [x] Hardwaretests (USB, Einhängepunkte, Netzlaufwerke): vom Nutzer am 18.10.2026 unter Punkt 4 abgehakt; keine zusätzliche Messung durch den Agenten. Die ältere lokale Laufzeitprüfung erfasste sieben Laufwerksbuchstaben.
- [x] Abnahme der Live-Diagnose einschließlich der offenen Hardwareprüfungen vom Nutzer am 18.10.2026 unter Punkt 4 abgehakt. Die ältere lokale Laufzeitprüfung erfasste sieben Laufwerke und eine LAN-Verbindung; WLAN wurde damals mit Testdaten geprüft.

## Modellvorschlag

- Günstig: **gpt-6-luna** für Bestandsaufnahme, Screenshotliste und Planpflege.
- Stark: **gpt-6.1-sol** für WPF-Layout, Größenlogik, Migration gespeicherter Einstellungen und EXE-Prüfung.
