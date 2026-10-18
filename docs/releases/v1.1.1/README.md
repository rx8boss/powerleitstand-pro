# PowerLeitstand PRO v1.1.1 – Prüfnachweise

- [Programm, ZIP und Prüfsummen im Release](https://github.com/rx8boss/powerleitstand-pro/releases/tag/v1.1.1)
- [Übersicht der sieben Designs](Designvergleich.png)

Die veröffentlichte EXE gehört zum Quellstand `142ac318b1bb3ebed8ed689fdc336856c813b5cc`.
Ihr SHA-256 lautet `adc00fa68aae1d1457409536a99ba17eef0dd0ee65491c8dda4fa5cb9fcb721d`.

Geprüft: 28 Kombinationen aus Design und fester Größe, Automatisch einschließlich eines kleinen Viewports, 70 Protokollkombinationen, Dialogzustände, 31 Autostart-Prüfungen und vorhandene Funktionstests. Ein isolierter Treiber mit dem produktiven Updatecode hat die veröffentlichte v1.1.1 von GitHub erkannt, heruntergeladen, anhand ihrer Prüfsumme verifiziert und die Test-EXE ersetzt. Anschließend ist die reale neue WPF-App gestartet und hat reagiert. Der ursprüngliche eigene Autostart-Eintrag wurde wiederhergestellt.

Grenzen: WPF-Aufnahmen bei 96 DPI; kein zusätzlicher Mehrmonitor-/DPI-Test, keine echte Windows-Anmeldung, kein manuelles Durchklicken des Updates aus der alten UI, kein echter Reparatur- oder Herunterfahrvorgang.

Der Microsoft-Defender-Scan konnte zweimal nicht abgeschlossen werden (Fehler `0x80508023`). Für diese EXE liegt daher kein bestandener Virenscan vor. Das ist ebenfalls keine Feststellung eines Malwarefundes.

Der Release ist auf GitHub unveränderlich. Deshalb liegen die nach dem abschließenden Downloadtest erstellten Zusatznachweise hier im Repository; EXE, ZIP und Prüfsummen bleiben die ursprünglichen Release-Dateien.
