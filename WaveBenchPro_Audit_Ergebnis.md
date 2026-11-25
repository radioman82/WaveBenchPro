# WaveBench Pro – Gegencheck des Code-Audits

**Erstellt:** 25.11.2026  
**Geprüfte Basis:** Repository `radioman82/WaveBenchPro`, Stand `61ad8c2` vor den Korrekturen  
**GitHub-Stand nach Korrekturen:** `1718e84` auf `main` und `feature/v0.3`  
**Ausgangsbericht:** `C:\Users\fred\Downloads\WaveBenchPro_Audit.md`

## Ergebnis in Kürze

Der Ausgangsbericht enthält eine Mischung aus bestätigten Fehlern, echten Einschränkungen und ungeprüften Verdachtsfällen. Die bestätigten, im aktuellen Umfang korrigierbaren Fehler wurden angepasst und mit Regressionstests abgesichert. Nicht reproduzierte Verdachtsfälle sind nicht als bewiesene Bugs eingestuft.

Die Test-Suite besteht mit **83 von 83 Tests**. Der Release-Build ist erfolgreich. Es gab keinen Live-Test mit WASAPI-Aufnahmehardware oder einem absichtlich fehlerhaften VST3-Plugin.

## Bewertung der 24 Befunde

| ID | Bewertung | Gegencheck und Ergebnis |
| --- | --- | --- |
| BUG-001 | Bestätigt und behoben | Die Multiplikation im Resampler lief vor der Umwandlung in `double` als `int` und konnte überlaufen. Multiplikation und Arraylängen werden jetzt mit sicheren Typen und geprüften Grenzen berechnet. Regressionstest für 44,1 → 48 kHz ergänzt. |
| BUG-002 | Bestätigt und behoben | Der Capture-Decoder wertete WAVEFORMATEXTENSIBLE nicht nach seiner SubFormat-GUID aus. PCM- und IEEE-Float-GUIDs werden nun erkannt; unbekannte Subformate werden abgelehnt. Ein realer USB-Interface-Test steht aus. |
| BUG-003 | Bestätigt und behoben | Aufnahmefehler des Geräts/Treibers wurden ignoriert. Sie werden jetzt weitergereicht; ein fehlendes Stop-Ereignis führt zu einem klaren Timeout statt zur stillen Übernahme einer möglicherweise unvollständigen Aufnahme. |
| BUG-004 | Bestätigt, RAM-Verbrauch reduziert | Aufnahmedaten landen nicht mehr während der gesamten Aufnahme in einem `MemoryStream` mit zusätzlicher Komplettkopie. WaveBench schreibt eine temporäre WAV und dekodiert blockweise. Für die vorhandene Einfüge-Architektur wird am Ende weiterhin ein Float-Samplepuffer im RAM benötigt. |
| BUG-005 | Bestätigt und behoben | Fehler beim Vorbereiten eines VST3-Effekts wurden verschluckt. Spur, Plugin und Fehler erscheinen nun als Hinweis; die Wiedergabe kann weiterhin mit übersprungenem Effekt starten. Kein fehlerhaftes Plugin live getestet. |
| BUG-006 | Bestätigt und behoben | Das Summensignal wurde im Mehrspurmixer hart auf ±1 begrenzt. Diese Verzerrung wurde entfernt; interne Float-Mixdowns behalten Headroom. PCM-Export begrenzt weiterhin entsprechend dem Zielformat. |
| BUG-007 | Bestätigt und behoben | Lineare Interpolation ohne Tiefpass verursachte Aliasing beim Herunterrechnen. Vor dem Downsampling wird jetzt ein bandbegrenzter FIR-Tiefpass angewendet; der Mehrspurmixer nutzt denselben Resampler. Ein Test prüft die Unterdrückung oberhalb der neuen Nyquist-Grenze. |
| BUG-008 | Verdacht nicht bestätigt | Der Bericht beschreibt mögliche Artefakte bei kurzen Clips und gegenphasigem Stereo, weist aber keinen reproduzierten Fehler nach. Der bestehende Sinustest prüft Tonhöhenstabilität; die genannten Sonderfälle wurden nicht abschließend messtechnisch untersucht. |
| BUG-009 | Verdacht nicht bestätigt | Es wurde kein erreichbarer Fehlerpfad für eine falsche Projekt-Samplerate gefunden. Leere Spuren verwenden die Projektzeitbasis; beim Hinzufügen einer Quelle wird sie in diese Zeitbasis umgerechnet. |
| BUG-010 | Bestätigt und behoben | Wiedergabe und Mixdown behandelten Solo auf leeren Spuren unterschiedlich. Beide werten Solo nun nur bei Spuren mit vorbereiteten Clips aus. |
| BUG-011 | Einschränkung, kein bestätigter Datenfehler | Der Renderer berechnet einen VST3-Tail, gibt die Bearbeitung aber in der bisherigen Länge der Auswahl zurück. Damit endet der Effekt-Ausklang am Auswahlende. Für längentreue Auswahlbearbeitung ist das eine bewusste Begrenzung; ein Export mit angehängtem Tail wäre eine zusätzliche Funktion. |
| BUG-012 | Bestätigt und behoben | Bei Monoquellen übernahm der VST3-Renderer nur den linken Plugin-Ausgang. Mono wird nun aus beiden Ausgängen gemittelt, sodass ausschließlich rechts ausgegebene Anteile erhalten bleiben. |
| BUG-013 | Bestätigt und behoben | Die WAV-Prüfung wertete nur einen Teil der Extensible-GUID aus und prüfte Chunk-Grenzen unvollständig. Die vollständige GUID, Zusatzdatenlänge und RIFF/Chunk-Grenzen werden geprüft; ein PCM-Extensible-Regressionstest wurde ergänzt. |
| BUG-014 | Vorsorglich abgesichert | Der Reader verwendet einen gemeinsamen Dateizeiger und Decode-Puffer. `ReadFrames` serialisiert diese Zugriffe jetzt pro Reader-Instanz. Ein paralleler Stresslauf wurde nicht ergänzt; die Sperre beseitigt den beschriebenen gleichzeitigen Zugriff auf diese Zustände. |
| BUG-015 | Bestätigt und behoben | Undo/Redo legte bis zu 20 komplette Samplekopien im RAM ab. Zustände werden nun temporär auf Platte gespeichert; die 20-Schritte-Grenze entfällt. Ein Test führt 25 Änderungen rückwärts und wieder vorwärts aus. Speicherplatz und Datei-I/O begrenzen die praktische Nutzung weiterhin. |
| BUG-016 | Bestätigt und behoben | Normales Speichern schrieb WAV-Dateien pauschal als 32-Bit-Float. WAVs mit 16/24-Bit-PCM und 32-Bit-Float behalten beim normalen Speichern ihre Bittiefe bzw. Float-Art. Regressionstests decken diese drei Formate ab. |
| BUG-017 | Kein bestätigter Fehlerpfad gefunden | Beim Schließen eines Dokument-Tabs werden Cache-Aufgaben beendet; `AudioDocument.Dispose` löscht aber nicht die Samples oder die Quellpfade, auf die Clips verweisen. Ein Sequencer-Test mit Tab-Schließen und anschließendem Mixdown wurde nicht als Regressionstest ergänzt. |
| BUG-018 | Beanstandeter Pfad nicht erreichbar | Der im Bericht genannte alte `RemoveTrack_Click`-Handler ist im aktuellen XAML nicht verdrahtet. Der sichtbare Spur-Entfernen-Button nutzt `RemoveTrackRequested`; dieser Pfad stoppt die Wiedergabe vor dem Entfernen. |
| BUG-019 | Beabsichtigtes Verhalten | Eine Auswahl wird automatisch wiederholt. Das entspricht der zuvor gewünschten Schleifenbedienung. Der separate Loop-Schalter ergänzt diese Funktion und ersetzt sie nicht. |
| BUG-020 | Bestätigt und behoben | Dokument und Cursorposition werden jetzt beim Start der Aufnahme festgehalten. Während der Aufnahme kann das Zieldokument nicht geschlossen werden; ein Tabwechsel ändert das Aufnahmeziel nicht. |
| BUG-021 | Bestätigt und behoben | Der Cursor nutzte vor der Formatumwandlung berechnete Aufnahmeframes. Nach dem Einfügen wird jetzt die tatsächliche Framezahl aus der Dokumentlängenänderung bestimmt. |
| BUG-022 | Risiko im UI-Pfad abgesichert | Vorbereiten der Mehrspurwiedergabe läuft im Hintergrund. Währenddessen ist der Inhaltsbereich gesperrt, damit UI-Bearbeitungen die gelesenen Quelldaten nicht parallel ändern. Ein unveränderlicher Daten-Snapshot und Abbruch der Berechnung sind nicht implementiert. |
| BUG-023 | Bestätigt und behoben | Ein verspätetes `PlaybackStopped`-Ereignis kann nur noch den aktuell zugeordneten Player beeinflussen; Ereignisse älterer Player werden ignoriert. |
| BUG-024 | Teilweise behoben | Mixdown und Export laufen nun per Hintergrundaufgabe statt synchron im UI-Thread. Währenddessen sind die Bedienelemente gesperrt. Normales Speichern bleibt synchron; Fortschrittsanzeige und Abbruchmöglichkeit sind nicht enthalten. |

## Verifikation

- `dotnet test tests\WellenEditor.Tests\WellenEditor.Tests.csproj --no-restore`: **83 bestanden, 0 fehlgeschlagen**.
- `dotnet build WellenEditor.csproj -c Release --no-restore`: **erfolgreich, 0 Fehler**.
- `git diff --check`: keine Whitespace-Fehler.
- GitHub-Branches `main` und `feature/v0.3` zeigen beide auf `1718e84bfc8bb476eec18714291be14ac037535d`.

Der Release-Build meldet weiterhin die vorhandenen NAudio-Warnungen zu `WasapiCapture`/`WasapiOut` sowie eine schon bestehende `async`-Methode ohne `await`. Diese Warnungen wurden in diesem Audit nicht umgebaut.
