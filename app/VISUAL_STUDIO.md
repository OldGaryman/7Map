# YAMEE in Visual Studio 2022

## Öffnen
1. Visual Studio 2022 starten (mindestens Version 17.12, installiert ist 17.14).
2. **Projekt oder Projektmappe öffnen** → `7Map\app\YAMEE.sln` wählen.
3. Rechts im **Projektmappen-Explorer** steht das Projekt `MapEditor` mit allen Dateien.

Falls Visual Studio beim ersten Öffnen meldet, dass „.NET-Desktopentwicklung“ fehlt:
im **Visual Studio Installer** auf „Ändern“ klicken und diese Workload anhaken.

## Starten und Fehler suchen
- **F5** = bauen und mit Debugger starten (Haltepunkte mit F9 auf eine Zeile setzen).
- **Strg+F5** = bauen und ohne Debugger starten.
- Oben in der Leiste **Debug** oder **Release** wählen:
  - *Debug*: zum Entwickeln, Programm landet in `MapEditor\bin\Debug\net9.0-windows`.
  - *Release*: wie `start_editor.bat`, Programm landet in `MapEditor\bin\Release\net9.0-windows`.

Beide Fassungen sind die Entwicklungs-Ausgabe „YAMEE (Dev)“ und benutzen dieselben
Einstellungen (`%APPDATA%\YAMEE-Dev\settings.json`). Die zuletzt geöffnete Welt
(`Maps\My World` im Release-Ordner) wird deshalb auch beim Start aus Visual Studio geladen.

**Achtung „Maps“-Ordner:** Der Projektordner „Maps“ ist in den Einstellungen relativ zur
exe angegeben. Mit *Debug* zeigen „Projekt öffnen …“ und „Welt laden und Projekt anlegen …“
deshalb auf `bin\Debug\net9.0-windows\Maps` (leer). Wer dort Projekte anlegen will, wählt
besser *Release* oder trägt in den Einstellungen (Reiter Pfade) den vollständigen Pfad ein.

## Wo was steht
| Datei / Ordner | Inhalt |
|---|---|
| `MainWindow.xaml` + `MainWindow*.cs` | Hauptfenster und seine Bedienung (aufgeteilt nach Themen) |
| `*Window.xaml` | weitere Fenster (Einstellungen, Gebäude, Spawnpunkte …) |
| `Theme.xaml` | alle Farben und Stile |
| `Controls\MapView.cs` | die Karte (Zeichnen, Zoom, Maus) |
| `Map\` | Rechnungen: Welt laden, Straßen planen, Gelände, Gebäude, Spielstände |
| `Languages\German.ini` | deutsche Übersetzung (Texte im Code sind englisch) |
| `MapEditor.csproj` | Projekteinstellungen (Version, Symbol, .NET 9) |

Regeln für den Code: Namen und Kommentare englisch; neue Texte englisch schreiben und
die deutsche Zeile in `German.ini` ergänzen (siehe `CLAUDE.md`, Abschnitt Karteneditor).

## Automatische Tests

Das Projekt `YAMEE.Tests` in der Projektmappe prüft wichtige Dinge automatisch, zum Beispiel:
Rückgängig/Wiederholen stellt die Karte exakt wieder her, Speichern und neu Laden ergibt dieselbe
Karte, lange Straßen haben keine Lücken, gleicher Startwert ergibt dieselbe Welt.

- **Starten:** Doppelklick auf `run_tests.bat` im Projektordner. Nach etwa 10 Sekunden steht da
  `OK  9 passed, 0 failed …` (grün) oder `ERRORS` mit den fehlgeschlagenen Tests (rot).
- In Visual Studio: rechts im Projektmappen-Explorer `YAMEE.Tests` mit rechts anklicken →
  „Als Startprojekt festlegen“, dann F5. Danach wieder `MapEditor` als Startprojekt wählen.
- Die Tests erzeugen eine kleine 2-km-Welt im Temp-Ordner und löschen sie am Ende.
  Eure Projekte und Einstellungen werden nicht angefasst.
- Nur einen Teil laufen lassen: `run_tests.bat Road` (alle Tests mit „Road“ im Namen).
- Ein Test mit echter Karte (Straßennetz, ca. 1 Minute) läuft nur, wenn vorher
  `set YAMEE_TEST_WORLD=<Projektordner mit Städten>` gesetzt ist; der Ordner wird nur gelesen.
- Neue Tests: in `YAMEE.Tests\Tests.cs` eine Methode mit `[Test("Name")]` anlegen und mit
  `Check.True`/`Check.Equal` prüfen.

## Zusammen mit Claude arbeiten
- Ändert Claude eine Datei, die in Visual Studio offen ist, fragt Visual Studio nach dem
  Neuladen („Ja“ bzw. „Alle ja“ wählen). Eigene, ungespeicherte Änderungen vorher speichern.
- Vor dem Bauen (in Visual Studio oder mit `start_editor.bat`) muss YAMEE geschlossen sein,
  sonst ist die exe gesperrt.
- Den Ordner `.vs` legt Visual Studio selbst an (persönliche Fensterdaten); er ist nicht im Git.
