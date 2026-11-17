# WaveBench Pro

Ein Windows-Audioeditor mit klassischer Edit-View-Oberfläche. Die Bedienung orientiert sich an den direkten Wellenform-Editoren der frühen 2000er Jahre und konzentriert sich auf die Einzelspurbearbeitung.

## Enthalten

- Menüleiste für Datei, Bearbeiten, Ansicht, Effekte und Optionen
- Organizer, Symbolleisten, Zeit- und Auswahlstatus
- Wellenformansicht mit Bereichsauswahl und Zoom
- Leertaste oder Tab startet und stoppt die Wiedergabe
- Wiedergabe, Ausschneiden, Kopieren, Einfügen, Löschen und Rückgängig
- Normalisieren, Einblenden und Ausblenden
- Auswahl der aktiven Windows-Wiedergabe- und Aufnahmegeräte unter `Optionen > Audio-Hardware`
- WAV-Export

## Starten während der Entwicklung

```powershell
dotnet run
```

## Portable EXE erzeugen

```powershell
dotnet publish -c Release
```

Die einzelne portable Datei liegt anschließend unter `bin\Release\net9.0-windows\win-x64\publish\WaveBenchPro.exe`.

## Bedienung

- Audiodatei öffnen oder in das Fenster ziehen
- Mit linker Maustaste einen Bereich markieren
- Rechte Maustaste setzt den Wiedergabecursor
- Mausrad zoomt die Wellenform
- Leertaste startet und stoppt die Wiedergabe
- Entf löscht die Auswahl, Strg+Z macht die letzte Änderung rückgängig
- Ausschneiden legt den Bereich für Einfügen am Cursor ab
