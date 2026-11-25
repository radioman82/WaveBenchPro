# WaveBench Pro – Ergebnis Arbeitsauftrag 05

**Datum:** 25.11.2026  
**Basis:** `be4d99dba9044955d5a04ba8380c4b9d1ebbfb9a`  
**Code-Commit:** `0acaf93`; auf `main` und `feature/v0.3` veröffentlicht. Die vorhandenen Git-Zugangsdaten mussten zunächst dem Konto `radioman82` zugeordnet werden.  
**Repository:** `radioman82/WaveBenchPro`; Branches `feature/v0.3` und `main`.

## BUG-047 – blockierte Aufnahmefinalisierung

**Bestätigt und für den Writer-Wartepfad korrigiert.** Der bisherige Aufruf `captureWriterTask.GetAwaiter().GetResult()` in `RecordingEngine.CloseWriter` wartete unbegrenzt, wenn der Aufnahmewriter nicht zurückkehrte. Ein injizierter Writer mit einem steuerbaren Gate reproduziert diesen Fall für `Finish` und `Cancel`.

Die Finalisierung wartet nun höchstens die konfigurierte Frist (normal drei Sekunden) auf den Writer. Bei Timeout nennt die Fehlermeldung die temporäre Datei und weist darauf hin, dass der Abschluss noch läuft. Die Datei wird weder verschoben noch gelöscht oder parallel geschlossen. Das Schließen des WAV-Writers liegt im Hintergrundauftrag und gehört zur überwachten Finalisierung. Erst nach Rückkehr und Schließen wird die Warteschlange freigegeben; eine neue Aufnahme wird bis dahin abgewiesen. Die temporäre WAV bleibt am gemeldeten Pfad erhalten. Wird das Fenster während einer Aufnahme geschlossen, läuft das Abbrechen im Hintergrund; bei ausstehender Finalisierung bleibt das Fenster offen und informiert den Benutzer.

Der Test `BlockedWriterTimesOutPreservesFileAndAllowsRestartAfterLateCompletion` prüft Stop und Cancel, Timeout, Dateierhalt, verweigerten Neustart, späte Rückkehr, lesbaren Inhalt und anschließenden Neustart. Bestehende Tests decken Treiberfehler, Stop-Timeout, Queue-Überlauf und Recovery ab. Testumgebung: Windows, .NET 9, künstlicher Capture und blockierter Test-Writer; die 50-ms-Frist im Test ist keine Hardwaremessung.

**Grenzen:** Ein hängender Treiberaufruf `StopRecording`/`Dispose` selbst ist nicht durch das Writer-Timeout abbrechbar. Bis zur Rückkehr des Writers kann die WAV noch einen unvollständigen Header besitzen. Erzwungenes Beenden des Prozesses oder Systemausfall währenddessen kann die Finalisierung verhindern; die Tempdatei wird nicht absichtlich entfernt, ihre Lesbarkeit ist dann nicht garantiert. Kein reales Interface, Dateisystemausfall oder GUI-Langzeittest wurde durchgeführt. Ein tatsächlich dauerhaft hängender Writer kann weitere Aufnahmen und das reguläre Schließen des Fensters verhindern; der Benutzer wird darüber informiert.

## BUG-044 – Architektur für längere Mehrspurprojekte

Der konkrete Entwurf mit blockweisen Quellen, zustandsbehaftetem Resampling und Stretch, begrenztem Cache, VST-Zwischendateien, atomarem Streaming-Export, Invalidierung, Abbruch und gestufter Abnahme steht in `WaveBenchPro_Speicherarchitektur_05.md`. Er ist zur Prüfung vorgesehen; die größere Umstellung ist noch nicht implementiert. Die bestehenden Speichergrenzen bleiben deshalb erhalten. Keine Vorher-/Nachher-Messung des Peak-Prozessspeichers oder der Laufzeit wurde für eine neue Streaming-Architektur durchgeführt, weil eine solche Implementierung noch nicht vorliegt. Die Messungen aus Ergebnis 04 beschreiben nur den bisherigen Speicherpfad und sind kein Nachweis für den Entwurf.

## Abnahme

- `dotnet test tests\WellenEditor.Tests\WellenEditor.Tests.csproj --no-restore`: **127 bestanden, 0 fehlgeschlagen**.
- Release-Build und `dotnet publish -c Release -r win-x64`: erfolgreich. Die vorhandenen NAudio-Veraltungswarnungen und die bestehende `async`-Warnung bleiben.
- `git diff --check`: vor Commit geprüft.
- Die EXE wurde unter `D:\develop\WellenEditor\bin\Release\net9.0-windows\win-x64\WaveBenchPro.exe` neu veröffentlicht. Ein manueller Wiedergabe-/Aufnahmetest der veröffentlichten EXE und Hardware-Dropout-Messungen wurden nicht ausgeführt.
- Undo, Auswahl-Schleife und längentreue Effektbearbeitung wurden in diesem Auftrag nicht umgebaut. Der Fahrplan 06–09 wurde nicht automatisch umgesetzt.
