using System.Text.RegularExpressions;

namespace PowerControlProWpf;

// Save both languages in backups and use the same text for the GitHub release.
internal static class AppReleaseNotes
{
    internal const string German = """
        Neu in Version 1.1.5 – Programme, Updates und Dialoge
        • Neon Blau, Pink und Grün nutzen auf allen vier Seiten, in den Unteransichten von Seite 4 und am Fensterrahmen den Effekt einer alten Leuchtröhre: kurzes Flackern beim Zünden, sanftes Pulsieren und gelegentliches Erlöschen mit erneutem Zünden. Beim Verschieben pausiert die Animation.
        • Der App-Updater prüft die SHA-256 der bereitgestellten Datei vor dem Austausch und erneut direkt vor dem Start. Fehlt die alte EXE nach einem teilweise fehlgeschlagenen Windows-Dateiaustausch, stellt der Updater sie aus der geprüften Sicherung wieder her.
        • Auf Windows 10 22H2 führt die ESU-Karte zu Windows Update und zeigt die erkannte Windows-Anmeldung. Eine manuell bestätigte Registrierung blendet die Karte auch nach einem Neustart aus; über die Optionen lässt sie sich wieder einblenden.
        • Der Autostart-Verwalter auf Seite 4 zeigt für den angemeldeten Benutzer direkt umschaltbare Einträge. Bei Windows- und Store-Apps wird das Startziel geprüft. Die Windows-Autostart-Einstellungen bleiben über einen eigenen Button erreichbar.
        • Seite 3 bündelt Windows-Diagnose und Reparatur in Werkzeugkacheln. Netzwerkdiagnose, IPv4-/IPv6-DNS-Profile, die Wiederherstellung der DNS-Einstellungen und die Prüfung alter Netzwerkadapter sind im Netzwerkbereich erreichbar. Diagnoseprotokolle können pro Werkzeug angezeigt werden.
        • Erkannte Paketmanager erhalten eine Deinstallationsaktion mit Angaben zum betroffenen Pfad und zu Paketdaten. Abbrechen, Schließen und Stopp erscheinen in den Einrichtungs- und Deinstallationsdialogen als Text ohne Kasten.
        • Die Paketmanager-Einrichtung verwendet einen Dialog mit Fortschrittsbalken in der App. Scoop meldet das Prüfergebnis sichtbar, NuGet prüft die Microsoft-Corporation-Signatur und Chocolatey startet über WinGet ohne MSI-Assistenten.
        • Unter „Programme → Mehr → Paketmanager“ lassen sich NuGet, PSResourceGet, Scoop und Chocolatey nach Bestätigung von Quelle und Ziel einrichten. Die App zeigt den Fortschritt und prüft die Verfügbarkeit. Für NuGet-Pakete lässt sich ein eigener Ordner auswählen.
        • Herunterfahren, Neustart, Abmelden und Sperren starten über das Menü im Infobereich direkt. Während eines Timers, einer Reparatur oder Installation sind diese Aktionen gesperrt.
        • Ein einzeln gewähltes, bestätigtes Programmupdate startet nach dem Klick ohne zusätzliche Auswahlbestätigung. Mehrfachauswahl, Erst-/Neuinstallation und Kanalwechsel bleiben bestätigungspflichtig. Versions-/Quellenprüfung und geordnetes Schließen bleiben vorgeschaltet.
        • Bestätigte Programmupdates verwenden den vorhandenen Installationsort ohne erneute Pfadabfrage. Das gilt auch für mehrere Updates und gemischte Auswahlen; Erst- und Neuinstallationen behalten ihre bisherigen Zielordneroptionen.
        • „Erneut prüfen“ ist in allen gemeinsamen Programminfos auch bei aktuellen, unklaren oder nicht installierten Einträgen erreichbar. Es wird nur dieses Programm geprüft; Suchtreffer nutzen denselben Weg. Vor bestätigten Updates werden eindeutig zugeordnete laufende Programme über Windows geordnet geschlossen. Die App wartet auf ihr Ende und prüft den ausgewählten Stand erneut; Speicherdialoge, verweigertes Schließen oder unklare Zuordnung verhindern den Installerstart. TeamSpeak behält seine getrennten Ausgaben. Offene Arbeit vor einem Update speichern.
        • Tukui erkennt die bestätigte WinGet-MSI-Paketkennung. Portable Standalone-Kopien bleiben getrennt: „Erneut prüfen“ lädt gezielt das offizielle Standalone-Paket, prüft echte EXE-Version und konkrete Entwicklersignatur und ermöglicht den gesicherten Austausch am vorhandenen Speicherort. Kein Paketdownload beim Start oder allgemeinen Hintergrundscan; die MSI-Versionsangabe wird nicht als Standalone-Update ausgegeben. Manuell abgelegte AdwCleaner-Dateien erhalten einen Hersteller-Updateweg mit veröffentlichter Prüfsumme, gültiger Malwarebytes-Signatur, Sicherung und Austausch am vorhandenen Speicherort.
        • Gespeicherte und unbestätigte Programmstände zeigen im Infodialog „Erneut prüfen“, auch ohne gespeicherten Updatehinweis. Dies gilt auch bei frisch geprüftem Bestand, wenn eine neuere Online-Version erkannt wurde, deren Updateweg noch nicht bestätigt ist. Ein bestätigtes Update bietet „Update installieren“ an. Prüfung, Ausführung und Abschlussbestätigung betreffen nur die ausgewählten Programme; ein erneuter Gesamtscan entfällt. Die Updateliste nimmt bekannte Versionshinweise zur gezielten Bestätigung auf. Erledigte Hinweise werden entfernt; andere gespeicherte Programme behalten ihren Prüfstand. Paketkennung, Zielversion und Installationsquelle werden vor dem Start erneut bestätigt.
        • Filee und Malwarebytes AdwCleaner ergänzen den Programmkatalog mit deutschen/englischen Beschreibungen, Herstellerlinks und bestätigten WinGet-Paketkennungen. Filee erscheint unter Tools und Büro/PDF, AdwCleaner unter Tools.
        • Der normale VLC-Eintrag erkennt registrierte 32-/64-Bit-Kopien mit Version und Speicherort. Eine Kopienauswahl hält Updates und Neuinstallation an der gewählten Architektur und Installerart. Die zusätzliche VLC-ZIP-Kachel und die ZIP-Auswahl für neue VLC-Installationen entfallen.
        • Die automatische NVIDIA- und AMD-Hotfixabfrage wurde entfernt. Unter „Treiber-Updates“ öffnet „Forum & Hotfix“ das NVIDIA-GeForce-Forum; die Kompatibilität eines Hotfixes muss im Beitrag geprüft werden. Die normale Windows-Treibersuche bleibt erhalten.
        • Die große Programmansicht zeigt sechs Spalten mit vier vollständigen Kachelreihen statt drei. Alle Designs verwenden denselben Aufbau; Kompakt behält drei Spalten und drei Reihen. Kachelhöhe und Scrollbereich passen sich gemeinsam an den verfügbaren Platz an.
        • Menü-, Kategorie- und Aktionsbuttons der Programminstallation erhalten einheitliche lokale Vektorsymbole. Konturen, Akzente und Bestätigung folgen den Farben des gewählten Designs; Beschriftungen und Klickflächen bleiben in Kompakt und Groß bedienbar. Programmlogos bleiben unverändert. Erneute Installationen werden in Vorbereitung, Fortschritt und Ergebnis eindeutig als Neuinstallation bezeichnet.
        • VLC-Neuinstallationen bleiben an die erkannte Kopie mit Architektur, EXE-/MSI-Installerart, Benutzerbereich und Speicherort gebunden. Ein passendes offizielles WinGet-Manifest wird vor Auswahl und Ausführung bestätigt; keine automatische Umstellung auf eine andere Variante. Die Abschlussprüfung unterscheidet nebeneinander vorhandene Kopien und protokolliert bei fehlender Bestätigung den konkreten Zuordnungsgrund.
        • Abbrechen, X oder Esc in der Installationsauswahl schließen auch die zugehörige Vorbereitung samt Ladekreis. Ohne gestartete Installation bleibt keine leere Abschlussanzeige zurück; die Auswahl wird wieder freigegeben. Ein bereits gestarteter Installer behält weiterhin seine Vorgangs- und Abschlussanzeige.
        • Die Installationsauswahl zeigt bei jedem Programm einen einheitlichen Bereich für den Installationsort. Bestätigte Installer erlauben Laufwerks- und Ordnerwahl je Programm; fest verwaltete Orte und vorhandene Installationen erhalten eine klare Erklärung. Gewählte Ziele werden vor dem Start erneut geprüft, sich überschneidende Ordner gesperrt und nach der Installation mit dem erkannten Ort verglichen. Ein Stop während der Zielprüfung startet keinen Installer mehr.
        • Ein mittiger Ladekreis begleitet den laufenden Programmvorgang, während Phasen und tatsächlicher Prozentfortschritt sichtbar bleiben. Einklappen und Minimieren pausieren die Animation; nach tatsächlichem Abschluss, Fehler oder beendetem Abbruch verschwindet sie.
        • Bestätigte OBS-Portable-Kopien werden als vollständiges offizielles Windows-x64-Paket aktualisiert. Beide Portable-Markierungen werden erkannt; Einstellungen, Szenen, Aufnahmen und zusätzliche Plugin-Dateien bleiben erhalten. Ursprungs- und Zielpaket, Prüfsummen und Dateizuordnung werden vor dem Austausch geprüft, das vorige Paket wird gesichert. Veränderte oder gesperrte Paketdateien stoppen den Vorgang; bei Austauschfehlern wird zurückgerollt. Der vorhandene Fortschrittsdialog zeigt Download und Installation.
        • Eine feine durchgehende Außenlinie hebt das Hauptfenster in der jeweiligen Design-Akzentfarbe hervor. Sie folgt den runden Ecken, bleibt beim Scrollen am Fensterrand und verändert weder Inhalt noch Bedienflächen.
        • Hauptfenster und eigene Dialogfenster erhalten passende gerundete Außenkonturen. Fenstermaße, Aktionen und Designfarben bleiben erhalten; das Hauptfenster benötigt dafür keine zusätzliche Fenstertransparenz.
        • Windows-Update-Installationen unterscheiden Vorbereitung, Prüfung der Auswahl, Download und Installation. Der Fortschrittsbalken entspricht der Programminstallation und zeigt bestätigte Prozentwerte mittig; Hintergrundprüfungen und verspätete Rückmeldungen überschreiben keinen aktiven Auftrag. Ohne Messwert bleibt die Anzeige animiert; beim Beenden wird weiter auf das tatsächliche Vorgangsende gewartet.
        • Online-Suchbeschreibungen verwenden vorhandene deutsche/englische Pakettexte zuerst. Fehlende Übersetzungen werden bei bestätigter Ausgangssprache erst in der geöffneten Programminfo über MyMemory ergänzt und für die Sitzung wiederverwendet. Originaltext und Quelle bleiben zugänglich; Netz-, Zeit- und Übersetzungslimits verändern keine Installations- oder Updatefreigabe.
        • Hinweisblasen schließen sicher, wenn ihr Fensterelement entfernt, ausgeblendet oder in ein anderes Fenster verschoben wird. Ein späterer Positionsaufruf kann die App dadurch nicht mehr wegen eines nicht angebundenen Visuals beenden; erneutes Anzeigen und die bestehende Gestaltung bleiben erhalten.
        • Die Programmansicht zeigt gespeicherte bestätigte Prüfdaten nach einem Neustart sofort mit Zeitangabe. Gespeicherte Updatehinweise werden zeitversetzt im Hintergrund bestätigt; bereits anderweitig aktualisierte Programme verlieren ihren alten Updatehinweis. Vor Aktionen bleibt eine frische Prüfung erforderlich. Teilprüfungen sind gekennzeichnet; der letzte vollständige Stand bleibt separat erhalten.
        • Erneutes Öffnen startet keinen vollständigen Programmscan. Bekannte Updatekandidaten werden lokal in Abständen nachgeprüft, Onlineversionen nach 15 Minuten. Parallele Prüfanfragen teilen sich einen Lauf; unabhängige GitHub-Abfragen beginnen früher und werden begrenzt parallel bestätigt. Fehlende Anbieterantworten gelten nicht als „keine Updates“.
        • Ereignisse zeigen kompakte Phasen statt Konsolen-, Prozent- und Commitlisten. Tatsächliche Fehler bleiben mit Zeit, Quelle, Vorgang und vorhandenem Fehlercode in einer eigenen errors.log erhalten; Wiederholungen eines Vorgangs werden gebündelt. Ereignis- und Fehlerdatei werden ab je 2 MiB geprüft archiviert, mit maximal drei datierten ZIP-Archiven je Art.
        • Scoop findet vorhandenes Git auch ohne current-/Shim-Verknüpfung. Die geprüfte Git-Datei wird nur für den gestarteten Prozess erreichbar gemacht; globale Suchpfade bleiben unverändert. Fehlende oder nicht nutzbare Git-Dateien erhalten eine konkrete Fehlermeldung.
        • Die untere Programm-Statuszeile zeigt eine neue Prüfung rot, frische Teilergebnisse orange und vollständig geprüfte Installationen samt Updates grün. Abbruch, Fehler und alte Prüfdaten erzeugen keinen grünen Abschluss; die Farben bleiben auch in hellen Designs lesbar.
        • Der Prüfdialog erklärt die Suche nach installierten Programmen und verfügbaren Updates. Rot zeigt die laufende Prüfung, Orange gefundene Teilergebnisse; nach Abschluss erscheinen die Ergebnisse unten mit grünem Status.
        • Start liest Einstellungen nur einmal; unveränderte Optionsdarstellung wird wiederverwendet. Benutzerabfragen laufen nicht doppelt und erhalten eine wirksame Zeitgrenze für Prozess und Ausgabe.
        • Seite 4 wechselt erst nach erfolgreichem Laden; abfangbare Ladefehler behalten die bisherige Seite und erlauben einen erneuten Versuch mit lokalem Fehlernachweis.
        • Seite 4 prüft Windows- und Treiberupdates beim ersten Öffnen gemeinsam. Die automatische Suche lässt sich abschalten und zeigt ihren Fortschritt direkt in der Übersicht.
        • Programminstallationen und verfügbare Updates gemeinsam prüfen; Chrome-EXE-Paket und Updateausgabe ohne Quellenspalte erkennen.
        • Bestätigte Updates über die gemeinsame Auswahl ausführen und die Zielversion danach erneut prüfen; andere Store-/Portable-Varianten bleiben getrennt.
        • Suchfeld mit kleinem begrenztem X, gerahmter Ladeanzeige, getrennten Suchtexten und Ergebnissen am Listenanfang.
        • Originalsymbole im Hintergrund nachladen; aktuelle Katalogsymbole behalten, nur eigene überholte oder temporäre Suchsymbole bereinigen.
        • Programminfo mit kurzem Speicherort und kleinem Ordnerbutton; offizielle Versionsquellen und manuelle Markierung nur bei unbestätigter Installation und Version.
        • Programminfo nach Vorlage mit größerer Schrift, klaren Versionszeilen, fester Fußzeile und aufklappbaren Paketdetails; Kompakt und Groß bleiben mittig ausgerichtet.
        • Programminfo zeigt bei bestätigter neuerer Version die installierte Version rot und die verfügbare Version grün; passende Updates erhalten eine eigene Aktion mit orangefarbenem Status.
        • Nicht erkannte Programme lassen sich über eine geprüfte EXE zuordnen. Dateizuordnungen werden gespeichert und erneut validiert; Store-/Paketversionen und Updatewege bleiben getrennt.
        • Programmorte werden zusätzlich über geprüfte Datei-, MSI-, Startmenü- und Herstellerhinweise ermittelt. Hauptdatei, Erweiterungsordner und registrierte Version bleiben getrennt; widersprüchliche Kopien werden nicht automatisch vermischt.
        • Scoop und Chocolatey prüfen vorhandene Programmdateien lesend auf Produkt, Hersteller und Version. Paketversionen und mehrere Kopien bleiben getrennt; die Liste installierter Programme zeigt auch Paketmanager-Einträge und weitere Store-Apps des aktuellen Windows-Kontos.
        • Bestätigte Scoop-/Chocolatey-Pakete erhalten eigene Installations- und Updatewege in der bestehenden Auswahl. Quelle, Version, Architektur und Installationskopie werden vor dem Start und nach dem Abschluss geprüft; gehaltene Pakete und unbestätigte Quellen werden nicht automatisch umgestellt.
        • Scoop-Vorbereitung wird separat bestätigt. Der App-Fortschritt nennt den Paketmanager, erklärt fehlende Downloadmeldungen und wartet beim Stoppen auf den gestarteten Vorgang. Benötigte Administratorrechte und Neustarts bleiben erkennbar; Neuinstallation nur für bestätigte unterstützte Paketarten.
        • Geöffnete Programminformationen übernehmen nachträgliche Store- und Updateergebnisse. Store-Prüfungen nutzen zuerst die Windows-Paketverwaltung, kennzeichnen Fehler und halten zuletzt bestätigte Paketdaten erkennbar.
        • Die Updateaktion der installierten EA App öffnet die geprüfte EA-Programmdatei. Aktualisierungen erfolgen in EA; ein nicht zugeordnetes WinGet-Setup wird nicht als Upgrade gestartet.
        • Programminfo bietet Update neben der Version sowie Neuinstallation und rote Deinstallation beim Status. Nicht installierte Programme erhalten keine Deinstallationsaktion; Katalog und Suchergebnisse nutzen dieselben Wege.
        • Neuinstallation prüft bestätigte WinGet-Pakete und unterstützte Hersteller-/GitHub-Dateien erneut. Dieselbe verfügbare Paketversion und die aktuelle Version bleiben ausdrücklich unterscheidbar; Dateiersatz erhält bestehende Einstellungen.
        • Nicht bestätigte oder nicht unterstützte Installationsquellen zeigen eine begründete Alternative beim vorhandenen Anbieter oder Store. Ein Fehler einer unabhängigen Store-Prüfung sperrt keine bereits bestätigte WinGet-Kopie.
        • Suchtreffer übernehmen bestätigte Paketkennungen und Paketversionen auch aus dem jüngeren Bestand. Deinstallation aus der Programminfo zeigt nur zugeordnete Installationen und verlangt weiterhin eine eigene Bestätigung.
        • „Installierte Programme“ erhält ein gezeichnetes Programmfenster-Symbol mit Haken. Ein mittiger Ladekreis zeigt die Bestandsabfrage an; Schließen bleibt möglich, und die Anzeige endet auch bei Fehler oder Abbruch.
        • TeamSpeak 3 und 6 bleiben unter einer Kachel mit Mehrfachauswahl und aufklappbaren Versions-/Pfadangaben. Eine vorhandene TeamSpeak 5 wird als Altinstallation erkannt; Beta- und Hauptausgabenwechsel werden ausdrücklich bestätigt.
        • TeamSpeak erhält eine Programmbeschreibung, erneute Updateprüfung sowie sichtbare Installieren-/Update-/Neuinstallationsaktionen. Offizielle Herstellerdownloads werden signaturgeprüft, im App-Fortschritt ausgeführt und anschließend erneut verifiziert.
        • Downloadmeldungen werden zuverlässiger übernommen, echte Prozentwerte angezeigt und fehlende Einzelmeldungen erklärt. Die TeamSpeak-Abschlussprüfung berücksichtigt auch fehlerhafte Versionsangaben.
        • Kompakte Installationsübersicht für einzelne und mehrere Programme; OBS Portable bei bestätigter Quelle und Prüfsumme mit eigener Konfiguration im gewählten Zielordner einrichten.

        Dialoge und Taskleistenbedienung
        • Statuspunkte im Hauptfenster und Schnellzugriff zeigen laufende Vorgänge einschließlich Reparaturbeendigung an; Hinweise folgen dem gewählten Design.
        • Gleich große Reparaturaktionen mit „Reparatur beenden“ und eindeutiger Bestätigung „Reparatur beenden“ / „Fortfahren“.
        • Einheitliche kompakte Dialogköpfe mit Blitz, diagonalen Streifen und kleinem Schließen-Symbol.
        • Benutzer-, Kennwort-, Bestätigungs-, Hinweis-, Fehler- und Reparaturdialoge an das gewählte Design angepasst.
        • Gemeinsame Buttonzustände für Hover, Drücken, Tastaturfokus und gesperrte Aktionen; Abbrechen und Schließen mit rotem Rahmen.
        • Reparaturergebnisse unterscheiden Erfolg, Abbruch und Fehler; bestehende Neustartauswahl bleibt erhalten.
        • Der Statuspunkt wird nach vollständig beendetem Abbruch wieder bereit, statt gelb zu bleiben.
        • Taskleistenmenü zeigt je nach Fensterzustand „Fenster minimieren“ oder „Fenster öffnen“.
        • Doppelklick auf das Taskleistensymbol öffnet das vorhandene Fenster; nachfolgende Klicks öffnen kein zusätzliches Menü.
        • Bestehende Funktionen, sieben Designs, deutsche und englische Bedienung bleiben erhalten.

        Enthaltene Korrekturen aus Version 1.1.4 auf Basis von 1.1.3
        • Bestehende Funktionen von Version 1.1.3 bleiben erhalten.
        • Installationsbestätigung mit Blitz, Streifen, Versionsvergleich und rotem Abbrechen-Button an alle Designs angepasst.
        • Deutsche und englische Dialogtexte einschließlich Wiederherstellung und älterer Versionen geprüft.
        • Benutzer-Buttons auf Seite 2 passen ihre Breite an Text und Icons an.

        Enthaltene Funktionen aus Version 1.1.3
        Updates und Wiederherstellung
        • Kompakte Updateübersicht mit installierter Version und kompatiblen GitHub-Versionen.
        • Änderungen per Button im selben Fenster öffnen; Zurück erhält die Auswahl.
        • Vollständige Versionshinweise auf Deutsch und Englisch, passend zur App-Sprache.
        • Unsichtbare Scrollleisten, Richtungspfeile und umbrochene Texte.
        • Downloadfortschritt, Dateiprüfung und Bestätigung vor einem Versionswechsel.
        • Frühere kompatible Veröffentlichungen auswählen und installieren.

        Programmsicherungen
        • Vor Updates und Wiederherstellungen die aktuelle EXE in AppData sichern.
        • Bis zu vier Sicherungen mit Version, Datum, SHA-256 und zweisprachigen Hinweisen.
        • Sicherungen prüfen, auswählen, wiederherstellen oder nach Bestätigung löschen.
        • Bei fehlgeschlagenem Start die vorherige EXE automatisch wiederherstellen.
        • Keine zusätzliche Backup-Datei neben der EXE; Einstellungen und Protokolle bleiben erhalten.
        • Zugriffskonflikt beim gleichzeitigen Lesen und Löschen einer Sicherung korrigiert.

        Schnellzugriff und Zuverlässigkeit
        • Taskleistenmenü mit Fenster öffnen, Zentrieren und bestätigten Systemaktionen.
        • Laufenden Timer im Schnellzugriff anzeigen und abbrechen.
        • Während eines Timers konkurrierende Systemaktionen und Beenden sperren.
        • Mehrfachstarts verhindern und die bereits laufende App in den Vordergrund holen.
        • Menüabsturz beim Schließen behoben; Fenster-Icon und Beenden-Rahmen verbessert.

        Darstellung und Sprache
        • Kompaktes Updatefenster mit Blitz, Streifen und angepassten kleinen Ansichten.
        • Kleine Zurück- und Schließen-Symbole; roter Hover mit weißem X.
        • Weißen Windows-Fensterstreifen entfernt, Größenänderung bleibt möglich.
        • Deutsche und englische Beschriftungen, Hinweise und eigene Fehlermeldungen überarbeitet.
        """;
    internal const string English = """
        New in version 1.1.5 – Programs, updates and dialogs
        • Neon Blue, Pink and Green emulate an old neon tube on all four pages, the page 4 subviews and the window outline. The light flickers briefly on ignition, pulses gently and occasionally goes out before relighting. The animation pauses while moving the window.
        • The app updater verifies the staged file's SHA-256 before replacement and again immediately before starting it. If a partial Windows file replacement leaves the old EXE missing, the updater restores it from the verified backup.
        • On Windows 10 22H2, the ESU card opens Windows Update and shows the detected Windows sign-in. Manually confirming enrollment hides the card across restarts; it can be shown again in Options.
        • The startup manager on page 4 shows entries the signed-in user can toggle directly. It checks launch targets for Windows and Store apps. Windows Startup settings remain accessible through a dedicated button.
        • Page 3 groups Windows diagnostics and repairs into tool tiles. The network section includes diagnostics, IPv4/IPv6 DNS profiles, DNS settings restoration and a check for old network adapters. Diagnostic logs can be viewed for each tool.
        • Detected package managers offer removal with details about the affected path and package data. Cancel, close and stop are text buttons in setup and removal dialogs.
        • Package-manager setup uses an in-app dialog with a progress bar. Scoop reports the check result, NuGet verifies the Microsoft Corporation signature, and Chocolatey runs through WinGet without the MSI wizard.
        • Set up NuGet, PSResourceGet, Scoop and Chocolatey under “Programs → More → Package managers” after confirming the source and destination. The app displays progress and checks availability. You can choose a separate folder for NuGet packages.
        • Shut down, restart, sign out and lock run immediately from the notification area menu. These actions are blocked during a timer, repair or installation.
        • A single selected, verified application update starts after the click without an additional selection confirmation. Batch selections, new installations, reinstalls and channel switches still require confirmation. Version/source verification and graceful application closure still run first.
        • Verified application updates use the existing installation location without asking for a destination again. This also applies to multiple updates and mixed selections; new installations and reinstalls retain their existing destination options.
        • Saved and unverified application states show Check again in the information dialog, even without a saved update notice. This also applies to freshly checked inventories when a newer online version is detected but its update route is not yet verified. A confirmed update offers Install update. Checking, execution and result verification cover only selected applications without another whole-catalog scan. The update picker includes known version notices for targeted verification. Resolved notices are cleared while unrelated saved applications retain their inspection state. Package identity, target version and installation source are revalidated before starting.
        • Filee and Malwarebytes AdwCleaner join the application catalog with German/English descriptions, publisher links and verified WinGet package identities. Filee appears under Tools and Office/PDF, AdwCleaner under Tools.
        • “Check again” is available in every shared program information card, including current, uncertain and uninstalled entries. Only that program is checked; search results use the same route. Before confirmed updates, uniquely matched running applications are closed gracefully through Windows. The app waits for exit and rechecks the selected state; save dialogs, refused closure or uncertain identity block the installer. TeamSpeak keeps its separate editions. Save your work before updating.
        • Tukui uses the verified WinGet MSI package identity. Portable standalone copies stay separate: “Check again” explicitly downloads the official standalone package, verifies its actual EXE version and exact developer signature, and allows backed-up replacement at the existing location. No package download on startup or general background scans; the MSI version is not presented as a standalone update. Manually placed AdwCleaner executables gain a publisher update route with a published checksum, valid Malwarebytes signature, backup and replacement at the existing location.
        • The normal VLC entry detects registered 32/64-bit copies with version and location. Copy selection binds updates and reinstallation to the selected architecture and installer type. The additional VLC ZIP tile and ZIP choice for new VLC installations have been removed.
        • Automatic NVIDIA and AMD hotfix searches have been removed. “Forum & Hotfix” under “Driver updates” opens the NVIDIA GeForce Forum; check hotfix compatibility in each post. The normal Windows driver search remains available.
        • The large program view shows six columns with four complete tile rows instead of three. All themes share this layout; compact keeps three columns and three rows. Tile height and the scroll viewport adapt together to the available space.
        • Program installation menus, categories and action buttons use consistent local vector icons. Outlines, accents and confirmation colors follow the selected theme; captions and click targets remain usable in compact and large layouts. Application logos are unchanged. Repeated installations are clearly labelled as reinstallation in preparation, progress and results.
        • VLC reinstallation stays bound to the detected copy's architecture, EXE/MSI installer type, scope and location. A matching official WinGet manifest is verified before selection and execution; no automatic switch to a different variant. Final verification distinguishes side-by-side copies and records a specific identity reason when completion cannot be confirmed.
        • Cancel, X or Esc in the installation selection also close its preparation panel and loading ring. Without a started installation, no empty completion card remains and selection is unlocked. An installer that has already started retains its operation and completion display.
        • Every program in the installation selection now has a consistent installation-location section. Confirmed installers allow a drive and folder per program; managed locations and existing installations have a clear explanation. Selected destinations are rechecked before execution, overlapping folders are blocked, and the detected location is compared after installation. Stopping during destination validation no longer starts an installer.
        • A centered loading ring accompanies the active program operation while phases and actual percentage progress remain visible. Collapsing or minimizing pauses the animation; it disappears after actual completion, failure or a finished cancellation.
        • Confirmed OBS portable copies update as complete official Windows x64 packages. Both portable markers are recognized; settings, scenes, recordings and additional plugin files are preserved. Original and target packages, checksums and file ownership are verified before replacement, and the previous package is backed up. Modified or locked package files stop the operation; replacement failures trigger rollback. The existing progress dialog shows download and installation.
        • A thin continuous outline highlights the main window in the current theme's accent color. It follows the rounded corners, stays at the window edge while scrolling and preserves content and interaction areas.
        • The main window and app dialogs use matching rounded outer contours. Window dimensions, actions and theme colors are preserved; the main window does not require additional window transparency.
        • Windows update installations distinguish preparation, selection validation, download and installation. The progress bar matches program installation and centers confirmed percentages; background checks and delayed callbacks cannot overwrite an active operation. Without a measurement the bar remains animated; stopping still waits for the operation to actually end.
        • Online search descriptions prefer existing German/English package texts. With a confirmed source language, missing translations are loaded through MyMemory only in the open program info and reused for the session. The original text and source remain accessible; network, timeout and translation limits do not change installation or update eligibility.
        • Tooltips close safely when their target is removed, hidden or moved to another window. A later placement callback can no longer terminate the app because of a disconnected visual; reopening and the existing appearance are preserved.
        • After a restart, the program view immediately shows saved verified inspection data with its timestamp. Saved update notices are verified later in the background; programs updated elsewhere lose their old update notice. Actions still require fresh verification. Partial checks are labeled; the last complete snapshot is preserved separately.
        • Reopening the view does not start a full program scan. Known update candidates are rechecked locally at intervals, and online versions after 15 minutes. Concurrent inspection requests share one run; independent GitHub queries start earlier and their routes are confirmed with bounded concurrency. Missing provider responses do not mean “no updates”.
        • Events show compact phases instead of console, percentage and commit lists. Actual errors remain in a separate errors.log with time, source, operation and available error code; repeated messages within an operation are grouped. Event and error files are verified and archived at 2 MiB each, keeping up to three dated ZIP archives per type.
        • Scoop can find existing Git even without a current/shim link. The verified Git executable is made accessible only to the launched process; global search paths remain unchanged. Missing or unusable Git receives a specific error message.
        • The lower program status line shows a new check in red, fresh partial results in orange, and fully checked installations and updates in green. Cancellation, errors and old results cannot produce a green completion; colors remain readable in light themes.
        • The check dialog explains that installed programs are scanned and available updates are checked. Red marks an active scan, orange marks partial results; after completion, results appear below with a green status.
        • Startup reads settings once and reuses unchanged options styling. User queries no longer overlap and have an effective timeout covering both the process and its output.
        • Page 4 opens only after successful initialization; recoverable loading errors preserve the current page and allow retry with local error details.
        • Page 4 checks for Windows and driver updates together when first opened. The automatic check can be turned off and shows its progress in the overview.
        • Check program installations and available updates together; recognize the Chrome EXE package and update output without a source column.
        • Run confirmed updates through the shared selection and recheck the target version afterwards; other Store/portable variants stay separate.
        • Search field with a small bounded X, framed loading notice, separate search captions and results starting at the top.
        • Load original icons in the background; retain current catalog icons and clean up only obsolete app-owned or temporary search icons.
        • Program information with a short location and small folder button; official version sources and manual confirmation only when both installation and version are unconfirmed.
        • Program information follows the reference with larger text, clear version rows, a fixed footer and expandable package details; compact and large views stay centered.
        • Program information shows the installed version in red and the available version in green when a newer version is confirmed; compatible updates get a dedicated action and orange status.
        • Unrecognized programs can be associated with a verified executable. File associations are saved and revalidated; Store/package versions and update routes remain separate.
        • Program locations also use verified file, MSI, Start menu and publisher hints. Main executables, extension folders and registered versions stay separate; conflicting copies are not combined automatically.
        • Scoop and Chocolatey read existing executables to verify product, publisher and version. Package versions and multiple copies stay separate; the installed-program list also shows package-manager entries and additional Store apps for the current Windows account.
        • Verified Scoop/Chocolatey packages gain dedicated installation and update routes in the existing selection. Source, version, architecture and installation copy are checked before execution and after completion; held packages and unconfirmed sources are not switched automatically.
        • Scoop preparation requires separate confirmation. In-app progress identifies the package manager, explains missing download reports and waits for a running operation when stopped. Required administrator privileges and restarts remain explicit; reinstall is limited to verified supported package types.
        • Open program information refreshes when delayed Store or update results arrive. Store checks prefer Windows package management, report failures and identify previously confirmed package data.
        • The installed EA app's update action opens the verified EA executable. EA handles its updates; an unmatched WinGet setup is not started as an upgrade.
        • Program information offers update beside the version, with reinstall and red uninstall actions beside the status. Uninstalled programs have no uninstall action; catalog and search results use the same routes.
        • Reinstallation rechecks confirmed WinGet packages and supported publisher/GitHub files. The same available package version and the current version remain explicitly distinct; file replacement preserves existing settings.
        • Unconfirmed or unsupported installation sources show an explained alternative through the existing provider or Store. An unrelated Store-check failure does not block an already confirmed WinGet copy.
        • Search results retain confirmed package identities and package versions from recent inventory. Uninstall from program information shows only matched installations and still requires separate confirmation.
        • “Installed programs” has a drawn application-window icon with a check mark. A centered spinner indicates inventory loading; closing remains available, and the indicator also stops on errors or cancellation.
        • TeamSpeak 3 and 6 share one tile with multiple selection and expandable version/location details. An existing TeamSpeak 5 is identified as a legacy installation; beta and major-edition switches require explicit confirmation.
        • TeamSpeak adds an application description, a fresh update check and visible install/update/reinstall actions. Official publisher downloads are signature-checked, run through the in-app progress view and verified afterwards.
        • Download reports are captured more reliably, measured percentages are shown and missing stage reports are explained. TeamSpeak completion checks also handle malformed version text.
        • Compact review for single and multiple installations; set up OBS Portable with a verified source and checksum and separate configuration in the selected destination.

        Dialogs and tray controls
        • Status indicators in the main window and tray show active operations including repair stopping; tooltips match the selected theme.
        • Equally sized repair actions with “Stop repair” and an explicit “Stop repair” / “Continue” confirmation.
        • Consistent compact dialog headers with a lightning icon, diagonal stripes and a small Close icon.
        • User, password, confirmation, information, error and repair dialogs match the selected theme.
        • Shared button states for hover, pressing, keyboard focus and disabled actions; Cancel and Close have red borders.
        • Repair results distinguish success, cancellation and errors; the existing restart choice is retained.
        • The status indicator returns to ready after cancellation has fully finished, instead of staying yellow.
        • The tray menu shows “Minimize window” or “Open window” according to the window state.
        • Double-clicking the tray icon opens the existing window; trailing clicks do not open an extra menu.
        • Existing features, seven themes, German and English controls are retained.

        Included corrections from version 1.1.4 based on 1.1.3
        • Existing version 1.1.3 features are retained.
        • Installation confirmation with lightning icon, stripes, version comparison and a red Cancel button matches every theme.
        • German and English dialog text checked, including restoration and older versions.
        • Page 2 user buttons adapt their width to their captions and icons.

        Included features from version 1.1.3
        Updates and recovery
        • Compact update overview with the installed version and compatible GitHub releases.
        • Open changes within the same window; going back preserves the selection.
        • Complete German and English release notes matching the app language.
        • Hidden scrollbars, direction arrows and wrapping text.
        • Download progress, file verification and confirmation before changing versions.
        • Select and install earlier compatible releases.

        Application backups
        • Save the current EXE in AppData before updates and restores.
        • Keep up to four backups with version, date, SHA-256 and bilingual notes.
        • Verify, select, restore or delete backups after confirmation.
        • Automatically restore the previous EXE if the new version fails to start.
        • No additional backup file beside the EXE; settings and logs are preserved.
        • Fixed a file access conflict when reading and deleting a backup concurrently.

        Quick access and reliability
        • Tray menu with open window, center window and confirmed system actions.
        • Display and cancel a running timer from quick access.
        • Block conflicting system actions and quitting while a timer is active.
        • Prevent multiple instances and bring the running app to the foreground.
        • Fixed a menu closing crash; improved the window icon and framed Quit button.

        Appearance and language
        • Compact update window with lightning icon, stripes and small-window layouts.
        • Small Back and Close icons; red hover effect with a white X.
        • Removed the white Windows frame strip while retaining resizing.
        • Revised German and English captions, hints and application error messages.
        """;
    internal const string Current = "## Deutsch\n" + German + "\n\n## English\n" + English;

    internal static string Select(string notes, bool english)
    {
        if (string.IsNullOrWhiteSpace(notes)) return english ? "No release notes stored for this version." : "Für diese Version sind keine Versionshinweise hinterlegt.";
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
        return (english ? "A translation is not available for this release. Original release notes:\n\n"
            : "Für diese Veröffentlichung fehlt eine Übersetzung. Original-Versionshinweise:\n\n") + notes.Trim();
    }
}
