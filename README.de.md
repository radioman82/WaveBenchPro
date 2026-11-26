# WaveBench Pro

[English](README.md) · [Bedienung](docs/UserGuide.md) · [Architektur](docs/Architecture.md)

Windows-Audioeditor mit klassischer Wellenformansicht und Mehrspur-Sequenzer. Entwickelt mit C#, WPF, .NET 9 und NAudio.

![Wellenformansicht](docs/images/editor.png)

## Download und Start

[GitHub Releases](https://github.com/radioman82/WaveBenchPro/releases) enthält vorhandene Entwicklungsstände mit Hinweisen. **v0.3.0-beta.2 wird als Entwurf vorbereitet**; Abnahme und Lizenzfreigabe stehen aus. Ältere Downloads können vom aktuellen Quellcode abweichen. Die unten beschriebenen Hardware-Backends sind unveröffentlichte Quellcodeänderungen und nicht in den eingefrorenen beta.2-Assets enthalten.

Das portable ZIP vollständig in einen beschreibbaren Ordner entpacken und **WaveBenchPro.exe** starten. Begleitdateien und Lizenzhinweise zusammenlassen. Die .NET-Laufzeit ist enthalten; zum Start wird kein SDK benötigt. Die Builds sind derzeit nicht signiert.

**Plattform:** Windows 11 x64 mit funktionierendem WASAPI-Audiogerät. Speicher- und Platzbedarf hängen vom Material und den Effekten ab. Die experimentelle Vorbereitung kann bis zu 16 GiB gemeinsamen Disk-Cache nutzen.

## Funktionen

- Auswahl, Ausschneiden, Kopieren, Einfügen, Löschen, Undo/Redo und Auswahl-Schleifen.
- WAV-Zugriff in Blöcken mit Wellenformanalyse im Hintergrund; komprimierte Importe werden in den RAM dekodiert.
- WAV, FLAC, MP3, Ogg Vorbis und Opus öffnen/exportieren, mit Formatparametern.
- Wellenform/Spektrogramm und getrennte horizontale/vertikale Zoomtasten mit Reset.
- Stereo-Pegelanzeige mit Peak, RMS, Peak-Hold und Clipping.
- WASAPI Shared als Standard, optional Exclusive-Wiedergabe und installierte ASIO-Treiber. Getrennte Ein-/Ausgabe, Kanalzuordnung und Aufnahme: [Audio-Hardware](docs/AudioHardware.md).
- Verstärkung, Normalisierung, Fades, EQ, Hoch-/Tiefpass, Kompressor und Peak-Clipper.
- Installierte VST3-Effekte scannen, bearbeiten, live abhören und offline anwenden.
- Dateien in Mehrspur ziehen: Spuren entstehen automatisch. Beat-/Taktraster, BPM, Clippositionen, Lautstärke, Panorama, Stumm und Solo.
- Spur-Inserts, verschiebbare Auswahl-Schleifen und blockweiser WAV-Mixdown.

![Mehrspuransicht](docs/images/multitrack.png)

## Experimentelle Wiedergabe

Unter **Optionen → Mehrspur: neue Wiedergabe (experimentell)** aktivieren. Private Spurdateien einschließlich Effekten werden vorbereitet; Sprünge/Loops lesen danach diesen kontinuierlichen Render.

Die Mixerregler benötigen keine neue Effektvorbereitung. Bereits gepufferte Daten verzögern die Reaktion: 4/8/16 Blöcke à 1024 Frames entsprechen bei 48 kHz etwa 85/171/341 ms, zuzüglich Ausgangspuffer. Die bisherige Engine bleibt Standard und manuell wählbarer Rückfall. Fehler werden gemeldet; Inserts werden nicht still umgangen.

## Bekannte Grenzen

- Mehrspurprojekte lassen sich noch nicht speichern/wieder öffnen. Mixdown exportieren und Quellen separat sichern.
- Eine vollständige Hardware-Langzeitabnahme steht aus.
- Nach Clip-/Effektänderungen werden alle belegten Spuren vorbereitet. Große Projekte können Minuten und viel Disk benötigen.
- RIFF-WAV-Grenzen gelten; RF64 fehlt. Exclusive-Aufnahme, ASIO-Loopback und abgenommenes Full-Duplex werden nicht angeboten.
- Native Plugins laufen im Programmprozess und können hängen/abstürzen. Nicht jedes VST3 ist kompatibel oder deterministisch.
- Der Peak-Clipper ist kein Lookahead-Limiter. BPM-Zahlen kommen aus Dateinamen; es gibt keine Beat-Erkennung.
- Startzeit, RAM, akustische Latenz und dauerhaft störungsfreie Wiedergabe sind nicht pauschal zugesichert.

Build-Befehle stehen im [englischen README](README.md#build-and-test) und in der [Architektur](docs/Architecture.md#build-and-packaging). Windows und das zu [global.json](global.json) passende .NET-9-SDK sind nötig.

Probleme nachvollziehbar über [Issues](https://github.com/radioman82/WaveBenchPro/issues) melden; private Pfade/Audiodateien weglassen. Es gibt keine zugesagte Reaktionszeit.

**Der Eigentümer hat noch keine Projektlizenz gewählt.** Öffentlicher Quellcode bedeutet keine allgemeine Wiederverwendungserlaubnis. [Lizenzstatus](docs/LicenseStatus.md) und [Drittanbieterhinweise](THIRD-PARTY-NOTICES.md) beachten. Der Beta-Entwurf ist noch nicht zur Weitergabe freigegeben.
