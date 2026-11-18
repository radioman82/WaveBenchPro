# WaveBench Pro

WaveBench Pro ist ein portabler Windows-Audioeditor für die direkte Einzelspurbearbeitung. Die Bedienung orientiert sich an klassischen Wellenform-Editoren: Datei öffnen, Bereich markieren, bearbeiten, exportieren.

## About

Aktueller Stand v0.4: Neben Undo/Redo, Aufnahme und WAV-/MP3-Export sind native Effekte mit Presets und Vorschau sowie VST3-Offline-Rendering enthalten. Die Oberfläche ist dunkel und auf Lesbarkeit getrimmt; beim Schließen oder Entfernen von Dateien mit ungespeicherten Änderungen fragt das Programm nach. Entwickelt als .NET-9-WPF-Anwendung mit NAudio.

## Enthalten

- Menüleiste für Datei, Bearbeiten, Ansicht, Effekte und Optionen
- Organizer, Symbolleisten, Zeit- und Auswahlstatus
- Wellenformansicht mit Bereichsauswahl, Zoom und gelber Markierung
- Wiedergabe und Aufnahme mit Pegelanzeige
- Ausschneiden, Kopieren, Einfügen, Löschen, Rückgängig und Wiederholen
- Sicherheitsdialoge: Speichern, Schließen ohne Speichern oder Abbrechen
- Native Effekte mit Presets und Vorschau: Verstärken, Normalisieren, Ein-/Ausblenden, EQ, Hoch-/Tiefpass, Kompressor und Limiter
- VST3-Scan und Offline-Rendering auf Auswahl oder gesamte Datei
- Export als WAV (PCM 16/24 Bit, 32-Bit-Float) und MP3
- Auswahl der aktiven Windows-Wiedergabe- und Aufnahmegeräte unter `Optionen > Audio-Hardware`
- Dunkle, lesbare Menüs und Dialoge

## VST3

VST3-Effekte werden über `Effekte > VST3-Effekte neu scannen` erkannt und per Doppelklick offline auf die Auswahl bzw. die ganze Datei angewendet. Instrumente werden übersprungen. Plugin-Oberflächen, Parametersteuerung, Presets und eine Insertkette sind noch nicht enthalten; die Plugins laufen mit ihrem Default-Zustand.

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
