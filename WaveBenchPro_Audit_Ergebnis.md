# WaveBench Pro – Gegencheck des Code-Audits

**Erstellt:** 25.11.2026  
**Geprüfte Basis:** Repository `radioman82/WaveBenchPro`, Stand `61ad8c2` vor den Korrekturen  
**GitHub-Basisstand der ersten Prüfung:** `1718e84` auf `main` und `feature/v0.3`
**Ausgangsbericht:** `C:\Users\fred\Downloads\WaveBenchPro_Audit.md`

## Ergebnis in Kürze

Der Ausgangsbericht enthält eine Mischung aus bestätigten Fehlern, echten Einschränkungen und ungeprüften Verdachtsfällen. Die bestätigten, im aktuellen Umfang korrigierbaren Fehler wurden angepasst und mit Regressionstests abgesichert. Nicht reproduzierte Verdachtsfälle sind nicht als bewiesene Bugs eingestuft.

Die erste Prüfung bewertete 24 Befunde. Eine ergänzende Prüfung zu BUG-025 bis BUG-032 wurde anhand der Folge-Mail vorgenommen. Die aktualisierte Test-Suite besteht mit **93 von 93 Tests**. Der Release-Build ist erfolgreich. Es gab keinen Live-Test mit WASAPI-Aufnahmehardware oder einem absichtlich fehlerhaften VST3-Plugin.

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

## Ergänzende Nachprüfung der Befunde 025–032

| ID | Bewertung | Gegencheck und Ergebnis |
| --- | --- | --- |
| BUG-025 | Ressourcenrisiko bestätigt; unbegrenztes Undo bleibt absichtlich erhalten | Ein Stereo-Snapshot mit 48 kHz und Float32 benötigt rund 1,3824 GB pro Audiostunde. Das ist die direkte Folge der gewünschten unbegrenzten, dateibasierten Undo-Historie und wurde deshalb nicht durch ein willkürliches Limit ersetzt. Undo-Dateien enthalten jetzt die Prozess-ID; beim nächsten Snapshot werden Dateien beendeter Prozesse entfernt. Aktuelle Undo-Dateien werden beim Dokument-Schließen weiter gelöscht. Sehr große oder sehr lange Sitzungen können weiterhin viel freien Datenträger benötigen. |
| BUG-026 | Bestätigt und optimiert | Der FIR-Tiefpass berechnete Sinus- und Kosinuswerte für jeden Eingangsframe erneut, obwohl die Filtergewichte nur von der Abtastratenumsetzung abhängen. Die Gewichte werden jetzt einmal pro Resampling-Aufruf vorberechnet und anschließend wiederverwendet. |
| BUG-027 | Bestätigt und behoben | WAVEFORMATEXTENSIBLE-PCM wurde bei `validBitsPerSample != containerBits` abgelehnt. PCM mit weniger gültigen Bits als Containerbreite wird jetzt akzeptiert; Werte größer als der Container werden als ungültig abgewiesen. Die bestehende PCM-Dekodierung berücksichtigt die linksbündige Ausrichtung. Ein 24-in-32-Regressionstest prüft die Dekodierung. Abweichende gültige Bitzahlen bei IEEE-Float bleiben ungültig. |
| BUG-028 | Bestätigt und behoben | Beim Speichern einer bearbeiteten Streaming-WAV über derselben Quelldatei blieben alte Segment-Offsets auf die nun verkürzte Datei gerichtet. Nach erfolgreichem Speichern wird das Dokument jetzt auf die geschriebene Datei und deren vollständige Timeline umgestellt. Streaming-Undo/Redo wird dabei gelöscht, weil diese Historie auf die ersetzte Quelldatei zeigte. Ein Regressionstest prüft die Audioframes nach In-Place-Speichern. |
| BUG-029 | Verdacht auf Datenverlust durch vollen Datenträger nicht bestätigt | Vor dem Laden eines Undo-Zustands wird zunächst der aktuelle Zustand als Snapshot geschrieben. Scheitert das wegen vollem Datenträger, fängt der Undo-Pfad den Fehler ab und legt den zuvor entnommenen Undo-Eintrag zurück. Der partielle neue Snapshot wird von `CreateSnapshot` entfernt. Ein künstlicher Test mit tatsächlich vollem Datenträger wurde nicht durchgeführt. |
| BUG-030 | Effektname war irreführend; Anzeige korrigiert | Die Implementierung begrenzt Sample-Peaks frameweise ohne Lookahead und ist kein transparenter Lookahead-Limiter. Menü und Effektanzeige nennen den Effekt jetzt „Peak-Clipper“. Die eigentliche DSP-Charakteristik bleibt unverändert. |
| BUG-031 | Bestätigt und behoben | Effektparameter wurden vor der DSP-Berechnung nicht durchgängig auf endliche und sinnvolle Bereiche geprüft. Native Effekte und Multitrack-Insert-Prozessoren verwenden jetzt dieselbe zentrale Validierung. Tests decken NaN, Unendlich, extreme Verstärkung und Frequenzen oberhalb der Nyquist-Grenze ab. |
| BUG-032 | Bestätigt und behoben | Beim normalen Speichern wurde 32-Bit-PCM als Float32 ausgegeben; Float64 wurde auf Float32 reduziert. Speichern in der Quell-WAV-Formatierung erhält jetzt PCM32 beziehungsweise Float64. Die WAV-Ausgabe unterstützt PCM32 und Float64 zusätzlich zu den bisherigen Formaten; Tests prüfen die erhaltene Kennzeichnung. |

### Verifikation der Ergänzungen

- `dotnet test tests\WellenEditor.Tests\WellenEditor.Tests.csproj --no-restore`: **93 bestanden, 0 fehlgeschlagen**.
- `dotnet build WellenEditor.csproj -c Release --no-restore`: erfolgreich.
- Die vorherigen Hinweise zu WASAPI-API-Veraltungen und der bestehenden `async`-Methode ohne `await` bestehen weiterhin; sie sind nicht Teil dieser Nachprüfung.

## Verifikation

- `dotnet test tests\WellenEditor.Tests\WellenEditor.Tests.csproj --no-restore`: **93 bestanden, 0 fehlgeschlagen**.
- `dotnet build WellenEditor.csproj -c Release --no-restore`: **erfolgreich, 0 Fehler**.
- `git diff --check`: keine Whitespace-Fehler.
- Die erste Runde wurde auf `main` und `feature/v0.3` bis `1718e84bfc8bb476eec18714291be14ac037535d` veröffentlicht. Die Ergänzungen 025–032 werden im Folgecommit der Repo-Historie dokumentiert.

Der Release-Build meldet weiterhin die vorhandenen NAudio-Warnungen zu `WasapiCapture`/`WasapiOut` sowie eine schon bestehende `async`-Methode ohne `await`. Diese Warnungen wurden in diesem Audit nicht umgebaut.
