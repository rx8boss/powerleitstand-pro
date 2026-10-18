# Versions- und Sprachprüfung vor jeder Veröffentlichung

1. Zielversion im Projekt festlegen. Die App liest installierte Version, Fußzeile und Update-User-Agent über `AppVersion`; Mindestversionen des Updateprotokolls nicht ändern.
2. Alle Änderungen seit dem letzten veröffentlichten Stand in `AppReleaseNotes.German` und `.English` pflegen. Beide Listen müssen vollständig sein und dieselben Änderungen enthalten. Keine automatische Übersetzung von Windows-Ausgaben oder fremden Release-Texten behaupten.
3. `scripts/verify-release.ps1 -PrepareNotes -Exe <neue EXE>` ausführen. Der Befehl bereitet `docs/releases/v<VERSION>.md` aus genau diesen beiden Texten vor und prüft Projekt-, Datei- und Produktversion. Die Prüfung muss erfolgreich sein.
4. `DesktopSmoke --languages` ausführen: Deutsch und Englisch, drei Seiten und Optionen, kleine/große Ansicht, Hauptdialoge, Protokollanzeige und sprachabhängige Versionshinweise aus GitHub-Testdaten sowie gespeicherten Sicherungen. Fachliche Gleichheit der Texte zusätzlich lesen; gleiche Anzahl an Punkten allein reicht nicht.
5. `DesktopSmoke --updates` und Downloadprüfungen durchführen. Aktuell/Update verfügbar/frühere Version durch Vergleichs- und Katalogtests prüfen. Neue EXE starten. Interne Prüfberichte ausschließlich lokal außerhalb des Repositorys speichern; niemals hochladen, verlinken oder in Release-ZIPs aufnehmen.
6. Erst nach gesonderter Freigabe die EXE und den vorbereiteten zweisprachigen Text veröffentlichen. GitHub-Tag muss exakt `v<VERSION>` lauten. ZIP vor dem Upload mit `-PublicZip <ZIP>` prüfen. Nur EXE, öffentliches ZIP und Prüfsummen veröffentlichen; interne Prüfunterlagen bleiben privat.
7. Frische GitHub-Release-JSON abrufen und `scripts/verify-release.ps1 -Exe <EXE> -GitHubReleaseJson <JSON>` ausführen. Damit werden veröffentlichter Tag, vollständiger Text und SHA-256 der hochgeladenen EXE geprüft. Ohne frische JSON darf die Prüfung nicht als GitHub-Veröffentlichungsprüfung gemeldet werden.

## Versionshinweise und Sicherungen

Die App zeigt den Abschnitt `## Deutsch` oder `## English` passend zur Sprache. Sicherungen enthalten beide Texte zusammen im bestehenden Metadatenfeld; ältere Sicherungen bleiben lesbar. Fehlt eine Sprachfassung, zeigt die App einen lokalisierten Hinweis und den unveränderten Originaltext. Ältere Veröffentlichungen werden nicht nachträglich erfunden oder stillschweigend übersetzt.

Historische Protokolle, Kontobeschreibungen, Gerätenamen und Windows-/DISM-/SFC-Ausgaben bleiben Originaldaten. Eigene Beschriftungen, Schweregradanzeigen und bekannte eigene Fehlermeldungen werden übersetzt. Ein Sprachwechsel verändert weder die gespeicherten Protokolle noch die internen Designkennungen.
