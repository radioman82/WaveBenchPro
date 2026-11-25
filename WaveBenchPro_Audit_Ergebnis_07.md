# WaveBench Pro – Arbeitsauftrag 07: Zwischenstand

Datum: 25.11.2026. Basis: `04194f29a38d6e80ec4ddd7dc60f15b77458e236`. Änderungen: `037ea9a`, `401fe4d`.

## Befunde

| Kandidat | Status und Nachweis |
|---|---|
| BUG-048 | Risiko bestätigt und teilweise abgesichert. Die Dateiidentität wird jetzt unmittelbar vor dem Ersetzen erneut geprüft. Ein Test erzeugt während des Exports einen Hardlink zur Quelle und prüft den Abbruch und den Originalinhalt. Ein separater Symlink-Test konnte nicht ausgeführt werden: Windows verweigerte das Erstellen eines Links wegen fehlenden Rechts. Zwischen Prüfung und Ersetzen verbleibt ein TOCTOU-Rennen. |
| BUG-049 | In zwei zusätzlichen Tests stimmen Länge und Samples bei längerer stummer Spur bzw. Solo, inklusive Stretch, mit dem bisherigen Renderer überein. Weitere Kombinationen und die gewünschte Semantik sind noch nicht vollständig abgenommen. |
| BUG-050 | Offen. Clipsuche und VST-Vorbereitung verwenden weiterhin lineare Suche; ein Viele-Clips-Benchmark und ein Intervallindex fehlen. |
| BUG-051 | Bestätigt und teilweise korrigiert. Ein simulierter Schreibfehler plus Fehler beim Löschen der Tempdatei erhält den ursprünglichen Fehler; die verbleibende Datei wird im Fehlerdetail genannt. Quellen werden beim Dispose auch nach Einzelfehlern weiter freigegeben. Test `CleanupFailureDoesNotHidePrimaryWriteErrorAndIdentifiesTemporaryFile`. Weitere Fehlerfälle sind noch zu prüfen. |
| BUG-052 | Bestätigt und teilweise korrigiert. `StopAsync` wartet begrenzt; ein blockierter Renderer wird nicht parallel entsorgt und später vom Worker freigegeben. Der Blockrenderer prüft Cancellation zwischen Spuren und Clips. Deterministische Tests prüfen blockierten und kooperativen Renderer. Ein bereits blockierender Lese- oder Pluginaufruf selbst ist nicht abbrechbar; CTS-Lebensdauer und alle Race-Szenarien bleiben offen. |
| BUG-053 | Offen. VST-Dateien bleiben privat pro Export mit 16-GiB-Grenze; gemeinsames Diskbudget, Reservierungen und Konkurrenztests fehlen. |
| BUG-054 | Teilweise definiert und getestet. Geräteframes zählen auch eingefügte Stille; Nutz- und Timelineframes zählen gelieferte Audioframes. Bei Unterlauf pausiert die Timeline, während das Gerät Stille ausgibt. Eine Cursor-/Latenz-Integration und weitere Teilblock-, EOF- und Fehlerprüfungen stehen aus. |

## Wiedergabeintegration

Der Ringpuffer bleibt ein interner Prototyp. Die vorhandene Engine ist weiterhin Standard; die experimentelle UI-Umschaltung, Prefill, Generationen, Seek/Loop, Live-Regler und sichere VST-Invalidierung sind noch nicht umgesetzt. Kein Hardwaretest oder Langzeitlauf wurde durchgeführt. Die im Auftrag verlangten acht unabhängigen nicht-sparse 60-Minuten-Quelldateien wurden noch nicht gemessen; die Werte aus Auftrag 06 sind kein Ersatz dafür.

## Prüfung und Veröffentlichung

- `dotnet test tests\WellenEditor.Tests\WellenEditor.Tests.csproj --no-restore`: 209 bestanden.
- Release-Publish für win-x64 erfolgreich; bestehende NAudio-Veraltungswarnungen und eine vorhandene async-Warnung bleiben.
- `git diff --check`: vor Commit erfolgreich. Kein manueller GUI- oder Hardwaretest der neuen EXE.
- Bericht 06 wurde über Google Drive für Desktop nach `H:\Meine Ablage\Projekte` übertragen und mit der lokalen Fassung verglichen.
- Arbeitsauftrag 07 ist **nicht abgeschlossen**. Die offenen Befunde und Abnahmepunkte oben sind die Fortsetzung; ASIO, Exclusive, RF64 und Projektformat wurden nicht geändert.
