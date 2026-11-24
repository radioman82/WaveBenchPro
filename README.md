# WaveBench Pro

Nativer Windows-Audioeditor mit klassischer Wellenformansicht und einer Mehrspur-Zeitleiste. Entwickelt mit C#, WPF, .NET 9 und NAudio.

## Aktueller Entwicklungsstand

- Einzeldateien bearbeiten: Auswahl, Ausschneiden, Kopieren, Einfügen, Trimmen, Undo/Redo und Loop-Wiedergabe.
- WAV-Dateien blockweise lesen; Wellenform-Vorschauen werden im Hintergrund berechnet. Andere unterstützte Formate werden beim Import dekodiert.
- WAV, FLAC, MP3, OGG und Opus importieren und exportieren; Exportparameter im Dialog einstellen.
- Wellenform und Spektrogramm; horizontale und vertikale Zoom-Tasten unten, jeweils mit eigenem Reset.
- Stereo-Pegelmesser mit Peak, RMS, Peak-Hold, dBFS-Zahlen und CLIP-Anzeige.
- Windows-Wiedergabe und Aufnahme über WASAPI; Geräteauswahl unter Optionen.
- Native Effekte: Verstärkung, Normalisierung, Fades, EQ, Hoch-/Tiefpass, Kompressor und Limiter.
- VST3-Scan, Plugin-Oberflächen, Live-Abhören und Offline-Anwendung im Einzeldatei-Editor; installierte Drittanbieter-Plugins werden nicht mitgeliefert.
- Mehrspur: Dateien aus dem Organizer auf die Zeitleiste ziehen; eine neue Spur entsteht beim Ablegen in einer freien Zeile.
- Beat-/Taktraster im 4/4-Takt, BPM-Einstellung und Tempoanpassung vorbereiteter Clips. Die Tempoerkennung liest eine BPM-Zahl aus passenden Dateinamen; es gibt noch keine automatische Audioanalyse des Tempos.
- Stumm/Solo, Lautstärke und Panorama pro Spur, verschiebbare Clips und Mixdown.
- Live verschiebbarer Loop-Bereich im Zeitlineal. Start, Ende und Dauer unten beziehen sich in Mehrspur auf diesen Bereich.
- Pro Spur ein FX-Rack mit Verstärkung, EQ, Hoch-/Tiefpass, Kompressor und Limiter. Zusätzlich können die gefundenen VST3-Effekte direkt als Inserts ausgewählt, konfiguriert, aktiviert/deaktiviert und entfernt werden. Diese Spur-Effekte verändern die Quelldateien nicht; VST3-Spureffekte werden beim Aufbau des Mehrspur-Streams angewendet.

Mehrspur ist weiterhin im Aufbau. Ein vollständiges Speichern und Wiederherstellen von Mehrspur-Projekten ist noch nicht implementiert.

## Entwickeln und testen

Voraussetzung: Windows und .NET-9-SDK. Alternativ kann das vorhandene lokale SDK unter `.tooling\dotnet\dotnet.exe` verwendet werden. Das SDK selbst gehört nicht ins Repository.

```powershell
dotnet restore .\WellenEditor.csproj
dotnet build .\WellenEditor.csproj -c Release
dotnet test .\tests\WellenEditor.Tests\WellenEditor.Tests.csproj
```

Die Entwicklungs-EXE liegt hier und benötigt die mitgebauten Dateien im selben Ordner:

```text
bin\Release\net9.0-windows\win-x64\WaveBenchPro.exe
```

`START_WaveBenchPro.cmd` öffnet genau diese EXE. DLLs in ihrem Ausgabeordner nicht einzeln entfernen.

## Portable Version

```powershell
dotnet publish .\WellenEditor.csproj -c Release -r win-x64 --self-contained true -p:PublishSingleFile=true -p:IncludeNativeLibrariesForSelfExtract=true
```

Die portable EXE liegt anschließend unter:

```text
bin\Release\net9.0-windows\win-x64\publish\WaveBenchPro.exe
```

Fertige portable Builds gehören als Assets in GitHub Releases, nicht in die Git-Quellhistorie. Die älteren `build_img.cmd`/`build_img.ps1` sind alternative Publish-Helfer mit Zielordner `Release`.

## Dateien und Aufräumen

Eine Bestandsaufnahme mit Hinweisen zu benötigten Dateien und entfernbaren Ausgaben steht in [docs/Ordnerstruktur.md](docs/Ordnerstruktur.md). Lokale Audiodateien, Samples, das SDK und Build-Ausgaben sind bewusst vom Git-Backup ausgeschlossen. Es werden keine Drittanbieter-VSTs oder Sample-Pakete hochgeladen.
