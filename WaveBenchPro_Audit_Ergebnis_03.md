# WaveBench Pro – Ergebnis Arbeitsauftrag 03

**Datum:** 25.11.2026  
**Repository:** `radioman82/WaveBenchPro`  
**Branch:** `feature/v0.3`  
**Code-Commit:** `7580d368d4746733907d16df4f28776e5dfe8c6a`  
**Basis:** `30c5422ed997231738308a2d8f780fb8252d1a14`

## Ergebnis

Der Auftrag hat mehrere konkrete Fehler bestätigt. Die Mehrspur-VST-Kette arbeitete nicht durchgehend in der sichtbaren Reihenfolge und verarbeitete Effekte pro Clip statt auf der gemischten Spur. Bei kurzen Clips konnte starkes Strecken nach dem Clipanfang Stille erzeugen. Die Aufnahme-Oberfläche blockierte beim Stoppen bis zu drei Sekunden, und Aufnahmefehler konnten die bereits geschriebene WAV löschen. Dazu kamen wiederverwendete Spurnummern sowie Rennen zwischen veralteten Cache-Aufgaben und der aktuellen Darstellung.

Die bestätigten Fälle sind korrigiert und mit Tests abgesichert. Unbegrenztes Undo, automatische Auswahl-Schleife und längentreue Effektbearbeitung bleiben erhalten.

## Neue bestätigte Befunde

| ID | Status und Ursache | Korrektur und Test | Restrisiko |
|---|---|---|---|
| **BUG-033 – Mehrspur-VST-Kette** | **Bestätigt, behoben.** `MultitrackWaveProvider.cs:96` renderte VST-Effekte clipweise vor der Mischung und abweichend von der Reihenfolge der FX-Liste. | Clips werden erst zur Spur summiert; danach läuft die gesamte native/VST-Kette in Listenreihenfolge. Wiedergabe und Mixdown verwenden denselben Renderer. `TrackEffectsTests.OrderedVstAndNativeEffectsProcessTheOverlappedTrackAndMatchMixdown` testet überlappende Clips und nicht vertauschbare Gain-/Clipper-Reihenfolgen. Zustandsänderungen veröffentlichen die neu vorbereitete Spur atomar. | Kein reales Drittanbieter-VST und keine Audio-Hardware live geprüft. VST-Spuren werden offline im Speicher gerendert; bei Spuren oberhalb des Array-Limits werden VST-Effekte übersprungen und gemeldet. Effekt-Tails bleiben zugunsten der bisherigen Länge abgeschnitten. |
| **BUG-034 – Aufnahmeverlust bei Fehlern** | **Bestätigt, behoben.** Timeout, Treiber-/Callback-Fehler, Decoderfehler oder fehlgeschlagenes Einlesen führten zuvor zum Löschen der temporären Aufnahme. | Fehleraufnahmen werden in `Dokumente\WaveBenchPro\Recovered` verschoben. Ist der Zielordner nicht verfügbar, bleibt die temporäre WAV unter ihrem gemeldeten Pfad erhalten. Ein explizites Abbrechen verwirft weiterhin. `RecordingEngineTests` simuliert Treiberfehler, Stop-Timeout, nicht unterstütztes Capture-Format und `OutOfMemoryException`; die gespeicherten Daten werden geprüft. |
| **BUG-035 – UI-Block beim Aufnahmeende** | **Bestätigt, behoben.** `RecordingEngine.Finish()` wartet auf das Stop-Ereignis; der synchrone Aufruf aus `MainWindow.Record_Click` konnte die Oberfläche bis zu drei Sekunden blockieren. NAudio 3.3.0 signalisiert das Ende aus seinem Capture-Thread; `StopRecording()` fordert das Ende an und ist nicht selbst die synchrone Wartezeit. | `Record_Click` wartet nun asynchron auf `Task.Run(recorder.Finish)`, verhindert doppeltes Stoppen und schließt das Fenster nicht parallel zur laufenden Finalisierung. NAudio-Ablauf: [WasapiCapture, NAudio v3.3.0](https://github.com/naudio/NAudio/blob/v3.3.0/src/NAudio.Wasapi/WasapiCapture.cs). | Der Capture-Callback schreibt weiterhin synchron in die WAV und berechnet Pegel. Langsame Datenträger können den Capture-Thread belasten; dafür wurde kein Dropout-Test mit realer Hardware ausgeführt. |
| **BUG-036 – kurze Clips beim Time-Stretch** | **Bestätigt, behoben.** Bei Samples unter 1.024 Frames und einem großen Stretch-Faktor konnte die bisherige 512-Frame-Overlap-Add-Schleife nur den Anfang kopieren; der Rest der verlängerten Ausgabe blieb null. | Sehr kurze Samples verwenden jetzt eine lineare, gemeinsame Stereo-Abbildung über die vollständige Clipdauer. Tests prüfen Faktor 0,1 und 10, endliche Werte, Samplezahl, Endsample und Gegenphasigkeit beider Kanäle. | Dieser Fallback ist Varispeed und verändert bei sehr kurzen Clips die Tonhöhe. Für solche Samples ist die alte Fenster-Methode nicht sinnvoll anwendbar; die Klangqualität muss beim Musikhören bewertet werden. Der allgemeine Verdacht aus BUG-008 ist damit nur für diesen reproduzierten Kurzclip-Fall bestätigt. |
| **BUG-037 – doppelte Spurnummern** | **Bestätigt, behoben.** Nach Entfernen einer mittleren Spur erzeugte `AddEmptyTrack()` eine Nummer aus `tracks.Count + 1`, die bereits vergeben sein konnte. | `MultitrackProject` vergibt jetzt fortlaufende Nummern, die im Projekt nicht wiederverwendet werden. Regressionstest: Spur löschen und neue Spur anlegen. |
| **BUG-038 – WAV-Lese-/Dispose-Rennen** | **Bestätigt, behoben.** `WavFileAudioSource.Dispose()` schloss den gemeinsamen Stream ohne dieselbe Sperre zu verwenden wie `ReadFrames()`. | Dispose wartet jetzt auf den laufenden Lesezugriff und ist idempotent; spätere Lesezugriffe erhalten `ObjectDisposedException`. Parallelzugriffe auf Dateiposition und Decode-Puffer werden durch einen Test mit parallelen Block-Lesezugriffen geprüft. |
| **BUG-039 – veraltete Wellenform-/Spektrogramm-Caches** | **Bestätigt, behoben.** Ein abgebrochener alter Cache-Task konnte nach einer Bearbeitung noch sein Ergebnis veröffentlichen oder den Build-Status eines neueren Spektrogramms zurücksetzen. | Peak- und Spektrogramm-Aufgaben verwenden jetzt Generationsnummern und Sperren beim Veröffentlichen. Cancellation-Tokens werden vor dem Hintergrundstart gesichert; alte Aufgaben dürfen neuere Ergebnisse nicht ersetzen. Fehler beim optionalen Spektrogramm lassen die Wellenform verfügbar. | Lese-/Cache-Tests und Release-Build liefen; ein länger laufender UI-Stresstest mit sehr großen WAVs wurde nicht durchgeführt. |
| **BUG-040 – Zoom bei maximaler Timeline-Länge** | **Bestätigt, behoben.** Der Zoom multiplizierte die Framezahl und wandelte sie vor dem Begrenzen in `int` um. Nahe `int.MaxValue` konnte der Wert überlaufen. | Der Zoomwert wird jetzt als `double` auf den gültigen Bereich begrenzt, bevor er nach `int` konvertiert wird. Regressionstest mit `int.MaxValue` und kleinstem Viewport. |
| **BUG-041 – Live-Vorschau bei Mehrspur-VST** | **Bestätigt, behoben.** Der VST-Dialog bot „Live“ an, aber die Mehrspur-FX-Ansicht hatte den Vorschau-Callback nicht verbunden. | Der Callback rendert die trockene ausgewählte Spur und spielt sie in einer Schleife durch genau die Plugin-Instanz im Dialog. Beim Schließen wird die Vorschau vor dem Plugin-Dispose gestoppt. | Mit einem echten VST-Plugin nicht live geprüft; das Verhalten ist durch Codepfad und erfolgreichen Build, nicht durch Hardwarewiedergabe belegt. |

## Nachprüfung älterer Befunde und Leistungswerte

- **BUG-028:** Der vorhandene Regressionstest speichert eine bearbeitete Streaming-WAV an derselben Stelle, öffnet sie erneut über das Dokument, vergleicht alle Samples und prüft den erwarteten Undo-Verlauf.
- **BUG-029:** Neu ergänzter injizierter Snapshot-Schreibfehler-Test. Bei einem fehlgeschlagenen Snapshot bleiben aktuelle Samples und der Undo-Eintrag erhalten; nach Ende des simulierten Fehlers funktioniert Undo.
- **BUG-008:** Der Kurzclip-Fall aus BUG-036 ist reproduzierbar. Für die übrigen behaupteten Artefakte bei Impulsen und gegenphasigem Stereo gibt es weiterhin keinen Nachweis eines generellen Qualitätsfehlers. Die neuen Tests sichern Samplelänge, endliche Werte und die Kanalbeziehung, nicht eine subjektiv artefaktfreie Klangqualität.
- **BPM, Loop, Clipgrenzen und Sampleraten:** Bestehende Tests decken Wiedergabe/Mixdown und die Loop-Grenzen ab. Ein zusätzlicher 500-facher Wechsel 120→140→120 BPM bleibt innerhalb eines Frames; Quell-/Projekt-Sampleraten werden weiterhin konvertiert.
- **BUG-026 Resampler-Benchmark:** 60 Sekunden Stereo, 48 kHz → 44,1 kHz, derselbe synthetische 440-Hz-/18-kHz-Testton. Baseline aus dem vorherigen Algorithmus: **7,50 s**, **43,7 MiB** verwaltete Allokationen, gemessener Prozess-Peak **93,1 MiB**. Optimierter Algorithmus: **0,50 s**, **42,3 MiB**, Peak **90,6 MiB**. Das entspricht hier etwa **15×** kürzerer Laufzeit. Der direkte Signalvergleich aus demselben Lauf ergab maximale Abweichung **3,6×10⁻⁷** und RMS-Abweichung **4,0×10⁻⁸**. Der separate Frequenztest für Alias-Unterdrückung bleibt grün.
- **BUG-022/024:** Kein neuer konkreter Fehler reproduziert. Mehrspur-Vorbereitung und Mixdown laufen im Hintergrund. Normales Speichern bleibt synchron; Fortschritt und Abbruch wären Verbesserungen, aber kein nachgewiesener Defekt dieses Auftrags.

## Nicht bestätigte Fehler und Grenzen

- VST3-Ressourcen werden über die vorhandenen Dispose-Pfade freigegeben; der Offline-Renderer verarbeitet in 4.096-Frame-Blöcken, kompensiert gemeldete Plugin-Latenz und begrenzt die Tail-Berechnung auf zwei Sekunden. Der bisherige Export schneidet das Ergebnis auf die ursprüngliche Auswahl-/Clip-Länge zurück.
- Ein fehlerhaftes natives Plugin kann den Prozess weiterhin abstürzen lassen oder hängen, weil Plugins im selben Prozess ausgeführt werden. Dafür wurde kein absichtlich defektes Plugin geladen; eine Prozess-Sandbox wäre ein größerer Architektur-Schritt.
- Das Gerät wird mit WASAPI im **Shared Mode** geöffnet (`MainWindow.xaml.cs` im Wiedergabe-Setup); ein Exclusive-Mode-Schalter und ASIO sind nicht vorhanden. Niedrige Latenz wurde nicht auf Hardware gemessen.
- `WasapiCapture` und `WasapiOut` sind in NAudio 3.3.0 als veraltet markiert; der Release-Build meldet diese sowie eine bereits vorhandene `async`-Methode ohne `await`. Sie wurden nicht auf die neuen Builder-APIs umgestellt.
- Es gibt keinen Hardwaretest für Aufnahme/Wiedergabe und keinen Live-Test mit einem Drittanbieter-VST3. Die vier Aufnahmefehlerszenarien sind kontrollierte Tests mit injizierter Capture-Schnittstelle.

## Verifikation und Veröffentlichung

- `dotnet test tests/WellenEditor.Tests/WellenEditor.Tests.csproj --no-restore`: **106 bestanden, 0 fehlgeschlagen**.
- `dotnet build WellenEditor.csproj -c Release --no-restore`: **erfolgreich, 0 Fehler** (bestehende NAudio-/Async-Warnungen siehe oben).
- `git diff --check`: **erfolgreich**.
- Code-Commit auf `feature/v0.3`: `7580d368d4746733907d16df4f28776e5dfe8c6a`.
- Der Ergebnisbericht ist als neue Markdown-Datei im Repository veröffentlicht und zusätzlich im Google-Drive-Ordner **Projekte** abgelegt.
