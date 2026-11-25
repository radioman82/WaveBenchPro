# WaveBench Pro – Ergebnis Arbeitsauftrag 04

**Datum:** 25.11.2026  
**Repository:** `radioman82/WaveBenchPro`  
**Basis:** `610799374635c9a8eb13210c682acdc1380dd843`  
**Code-Commit:** `3ae5d46793b7b78b808700be446bca96bf17ff95`; zusätzlicher Bypass-Test in `6b2b88e`  
**Branches:** `feature/v0.3` und `main`

## Bestätigte Befunde

### BUG-042 – VST-Eingabe und Ausgabe

**Bestätigt und korrigiert.** Bei einer Stereo-Quelle, die nur ein einzelnes Sample pro Leseaufruf lieferte, blieb die alte Schleife in `Vst3OfflineRenderer.cs` ohne Fortschritt. Der Renderer sammelt nun Teillesungen, prüft Kanalzahl und Samplerate, berechnet Arraygrößen geprüft und meldet eine vorzeitig endende Quelle oder unvollständige Plugin-Ausgabe. Eine stille Mischung aus bearbeitetem und trockenem Signal findet nicht mehr statt. `Vst3OfflineRendererTests` prüft Teillesungen mit Zeitlimit, unvollständige Frames, falsches Format und Überlauf; `TrackEffectsTests.MixdownFailsInsteadOfSilentlyExportingDryAudioWhenVstFails` prüft den Exportfehler. Restrisiko: Plugin-spezifische Latenz und Tail-Verarbeitung sind nicht umfassend mit unterschiedlichen Plugins vermessen; die Ausgabe bleibt bewusst auf die Eingabelänge begrenzt.

### BUG-043 – veraltete Mehrspur-Effektvorbereitung

**Bestätigt und korrigiert.** `MultitrackWaveProvider.cs:30`: Wurde ein VST hinzugefügt und anschließend entfernt, während die erste Vorbereitung noch lief, konnte deren Ergebnis später wieder veröffentlicht werden. Jede Änderung erhält jetzt eine neue Generation; Vergleich und Veröffentlichung erfolgen unter derselben Sperre. Clippositionen, Dauer und Effektzustände werden für die Aufgabe kopiert, veraltete Ergebnisse und Warnungen verworfen und überholte Aufgaben nach Möglichkeit abgebrochen. Der deterministische Gate-Test `RemovingVstWhilePreparationIsBlockedPreventsStaleStatePublication` bildet die Reihenfolge ohne Sleeps nach. Restrisiko: Ein gerade laufender nativer Plugin-Aufruf kann erst nach seiner Rückkehr auf Abbruch reagieren; Audio-Dokumente selbst sind nicht tief kopiert.

### BUG-044 – Speicherbedarf und stiller VST-Bypass

**Bestätigt und teilweise begrenzt.** `MultitrackWaveProvider.cs:125`, `MultitrackProject.cs:72`, `Vst3OfflineRenderer.cs:20`: Die bisherige Vorbereitung materialisierte Clips und komplette Spurtimelines; ein fehlerhafter VST-Effekt konnte beim Mixdown unbemerkt übersprungen werden. Vor der Vorbereitung stehen jetzt Schätzungen und Grenzen von 256 MiB pro vorbereiteter Spur, 512 MiB pro Projekt und 256 MiB für den einzelnen Mixdown-Puffer. Die Vorschau meldet einen ersatzweisen Signalweg, wenn die VST-Vorbereitung die Grenze überschreitet; der Mixdown bricht bei VST-Fehler oder Überschreitung ab. Die dynamisch wachsende Ausgabeliste und Blockkopien im VST-Renderer entfallen. Tests prüfen einen weit hinten platzierten Clip, mehrere große Spuren und den abgebrochenen Mixdown. Restrisiko: Die Grenzen sind Schätzungen, keine harte Obergrenze für den gesamten Prozess oder Plugin-Speicher; lange Mehrspurprojekte werden dadurch derzeit abgelehnt. Blockweises Rendering mit begrenztem Cache und Streaming-Mixdown ist als spätere Architekturänderung sinnvoll, aber nicht umgesetzt.

### BUG-045 – Aufnahme-Callback blockiert auf Disk

**Echtzeitrisiko bestätigt und entschärft.** `RecordingEngine.cs:205`: Der Capture-Callback schrieb zuvor synchron in die WAV-Datei und dekodierte den Pegel. Er kopiert jetzt Daten in gepoolte Puffer und eine begrenzte Warteschlange aus acht Blöcken; ein Hintergrundprozess schreibt und berechnet den Pegel. Bei vollem Puffer wird die Aufnahme gestoppt, ein Fehler gemeldet und die bisherige WAV zur Wiederherstellung gesichert. Die Tests blockieren den injizierten Writer deterministisch und prüfen Rückkehr des Callbacks, vollständige Reihenfolge sowie Überlauf und Recovery; vorhandene Tests prüfen Stop-Timeout, Treiberfehler und Decoderfehler. Restrisiko: Der Callback verwendet weiterhin Pool und Queue und ist keine garantierte Zero-Allocation-Echtzeitimplementierung. Stop/Cancel können beim Entleeren auf einen dauerhaft blockierten Datenträger warten. Ein Aufnahme- und Dropout-Test mit realem Interface wurde nicht durchgeführt.

### BUG-046 – Segmentgrenzen und Suche

**Grenz- und Leistungsrisiko bestätigt und abgesichert.** `SegmentedAudioSource.cs:27`: Segmentenden und Gesamtlänge werden geprüft, negative und außerhalb der Quelle liegende Bereiche sowie nicht endliche Gain-Werte abgewiesen. Die Segmentauswahl verwendet binäre Suche. Der Editor erzeugt diese Segmente beim Streaming-Bearbeiten und bei Undo/Redo, daher ist der Pfad grundsätzlich erreichbar; ein konkreter normaler Bedienablauf, der einen Überlauf erzeugt, wurde nicht nachgewiesen. Tests prüfen Bereichs- und Überlaufgrenzen sowie viele Segmente. Restrisiko: Die Messung ist ein künstlicher Speichertest und kein langer GUI-Bearbeitungslauf.

## Weitere Prüfungen und Grenzen

- **WAV:** RF64 wird mit verständlicher Meldung abgewiesen; ungerade Chunks mit Pad-Byte werden getestet. Nicht endliche Float-Samples werden beim Lesen des jeweiligen Blocks abgewiesen; eine Streaming-Datei wird dafür nicht beim Öffnen vollständig durchsucht. BWF- und andere Zusatzmetadaten werden beim Neuschreiben nicht erhalten. RF64/BWF-Unterstützung wurde nicht ergänzt.
- **Speichern am Quellpfad:** Das normale Speichern einer bearbeiteten Streaming-WAV am selben Pfad wird durch den vorhandenen Test geprüft. Der Export einer Auswahl über die offene Quelldatei wird nun abgewiesen, da das geöffnete Dokument sonst auf eine verkürzte Quelle zeigen würde; `SaveRangeAsWaveCannotReplaceItsOpenSourceFile` prüft Original und Dokumentlänge.
- **DSP und Transport:** Ein zustandsbehafteter Hochpass liefert bei Start am Timeline-Anfang im Test dieselben Samples für Wiedergabe und Mixdown. Aktuell bleiben DSP-Zustände über Loop und Rewind erhalten; stummgeschaltete oder durch Solo ausgeschlossene Spuren verarbeiten währenddessen nicht und ihr Zustand bleibt stehen. Ein Start mitten im Clip beginnt mit frischem Filterzustand. Ob diese Semantik für alle Effekte musikalisch gewünscht ist, ist offen; ein genereller Fehler wurde daraus nicht abgeleitet. Bestehende Tests prüfen Frequenzbereiche, Parametergrenzen, Kompression und kurze Stretch-Clips. Eine vollständige Messreihe zu Zeitkonstanten, FFT-Skalierung und allen Extremwerten steht aus. Kurze Stretch-Clips nutzen Varispeed und verändern die Tonhöhe.
- **VST3:** Lokale Einmaltests mit installierten Kilohearts Gain und Trance Gate erzeugten endliche, längentreue Offline-Ausgaben. Gain akzeptierte SaveState/LoadState ohne Fehler; die Wiederherstellung einzelner Parameter wurde nicht verglichen. Ein automatischer Bypass-Test prüft, dass ein deaktivierter Effekt nicht aufgerufen wird. Plugin-UI, Live-Vorschau, tatsächliche Latenzkompensation, Tail und Rhythmus wurden nicht live durchgehört oder umfassend vermessen. Plugins laufen im Hostprozess; Sandbox, Sidechain und Automation sind getrennte Architekturthemen.
- **NAudio:** Wiedergabe verwendet weiterhin WASAPI Shared. `WasapiOut` und `WasapiCapture` sind in NAudio 3 als veraltet markiert; die Bibliothek empfiehlt `WasapiPlayerBuilder` und `WasapiRecorderBuilder`. Ein Wechsel braucht Geräte-, Format-, Stop- und Recovery-Tests und wurde nicht vorgenommen. Exclusive Mode ist über den Player-Builder grundsätzlich möglich, erfordert aber Format-/Geräteprüfung; ASIO benötigt einen passenden Treiber und gesonderte Integration. Keine Hardwarelatenz wurde gemessen. Quellen: [NAudio Release Notes](https://github.com/naudio/NAudio/blob/main/RELEASE_NOTES.md), [WasapiPlayer](https://github.com/naudio/NAudio/blob/main/Docs/WasapiPlayer.md), [WasapiRecorder](https://github.com/naudio/NAudio/blob/main/Docs/WasapiRecorder.md).
- **Projektdatei – Konzept:** Ein versioniertes Format sollte Tempo, Spuren, Clips, relative Quellpfade, Mixer- und Effektzustände enthalten. Speicherung über temporäre Datei und atomaren Austausch, fehlende Medien beim Öffnen verständlich melden und Recovery getrennt verwalten. Noch nicht implementiert. Fortschrittsanzeige, Abbruch und Warnung bei knappem Undo-Speicher sind separate Verbesserungen.

## Messungen und Abnahme

Messung am vorhandenen Windows-Rechner mit .NET 9.0.23, Debug-Testprozess; Einzelwerte sind Momentaufnahmen ohne isolierte Peak-Working-Set-Messung:

| Probe | Ergebnis |
|---|---|
| 60 Sekunden, 48 kHz, simuliertes VST-Passthrough | 147 ms, 66,0 MiB verwaltete Allokationen auf dem aufrufenden Thread |
| Clip bei Frame 40.000.000 | vor Timeline-Allokation abgelehnt; 1,1 KiB Allokation beim Prüfschritt |
| 10.000 zufällige Zugriffe auf 50.000 Segmente | 14,9 ms |
| Capture-Callback bei blockiertem Test-Writer | 0,01 ms, 136 Bytes auf dem aufrufenden Thread; kein Hardwaretest |

- `dotnet test tests\WellenEditor.Tests\WellenEditor.Tests.csproj --no-restore`: **125 bestanden, 0 fehlgeschlagen**.
- `dotnet build WellenEditor.csproj -c Release --no-restore`: erfolgreich; die vorhandenen NAudio-Veraltungshinweise und eine `async`-Warnung bleiben.
- `dotnet publish ... -c Release -r win-x64`: erfolgreich. EXE: `D:\develop\WellenEditor\bin\Release\net9.0-windows\win-x64\WaveBenchPro.exe`. Ein manueller Wiedergabe- oder Aufnahmetest der veröffentlichten EXE wurde in diesem Durchlauf nicht ausgeführt.
- `git diff --check`: erfolgreich. Unbegrenztes Undo und automatische Auswahl-Schleife wurden nicht eingeschränkt; VST-Ausgabe bleibt längentreu.
