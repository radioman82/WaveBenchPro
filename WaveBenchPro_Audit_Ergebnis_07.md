# WaveBench Pro – Arbeitsauftrag 07: Zwischenstand

Datum: 25.11.2026. Basis: `04194f29a38d6e80ec4ddd7dc60f15b77458e236`. Änderungen: `037ea9a`, `401fe4d`, `bacd014`, `726c9e8`, `1a43bf0`.

## Befunde

| Kandidat | Status und Nachweis |
|---|---|
| BUG-048 | Risiko bestätigt und teilweise abgesichert. Die Dateiidentität wird jetzt unmittelbar vor dem Ersetzen erneut geprüft. Ein Test erzeugt während des Exports einen Hardlink zur Quelle und prüft den Abbruch und den Originalinhalt. Ein separater Symlink-Test konnte nicht ausgeführt werden: Windows verweigerte das Erstellen eines Links wegen fehlenden Rechts. Zwischen Prüfung und Ersetzen verbleibt ein TOCTOU-Rennen. |
| BUG-049 | In zwei zusätzlichen Tests stimmen Länge und Samples bei längerer stummer Spur bzw. Solo, inklusive Stretch, mit dem bisherigen Renderer überein. Weitere Kombinationen und die gewünschte Semantik sind noch nicht vollständig abgenommen. |
| BUG-050 | Skalierungsrisiko bestätigt und abgesichert. Beide Pfade verwenden einen sequenziellen Intervallindex, der aktive Clips in ursprünglicher Summierreihenfolge liefert. Ein Test vergleicht 2.000 zufällige Intervalle mit linearer Suche; 20.000 dünn verteilte Clips/Blöcke benötigten im isolierten Release-Test 22,9 ms für den Indexlauf. Kein Vergleichswert der alten Suche wurde gemessen. |
| BUG-051 | Bestätigt und teilweise korrigiert. Ein simulierter Schreibfehler plus Fehler beim Löschen der Tempdatei erhält den ursprünglichen Fehler; die verbleibende Datei wird im Fehlerdetail genannt. Quellen werden beim Dispose auch nach Einzelfehlern weiter freigegeben. Test `CleanupFailureDoesNotHidePrimaryWriteErrorAndIdentifiesTemporaryFile`. Weitere Fehlerfälle sind noch zu prüfen. |
| BUG-052 | Bestätigt und teilweise korrigiert. `StopAsync` wartet begrenzt; ein blockierter Renderer wird nicht parallel entsorgt und später vom Worker freigegeben. Der Blockrenderer prüft Cancellation zwischen Spuren und Clips. CTS wird nach Workerende und Dispose freigegeben; Fehler bei Render und Dispose bleiben erhalten. Deterministische Tests prüfen blockierten und kooperativen Renderer. Ein bereits blockierender Lese- oder Pluginaufruf selbst ist nicht abbrechbar; umfassende Race-Szenarien bleiben offen. |
| BUG-053 | Teilweise korrigiert. Standard-VST-Sitzungen reservieren unter einem prozessübergreifenden Dateilock gemeinsam bis zu 16 GiB und prüfen freien Speicher. Aktive Leases bleiben geschützt; Tests decken konkurrierende Reservierungen, Budgetüberschreitung und Bereinigung ab. Das Budget garantiert keinen freien Speicher gegenüber anderen Programmen; Prozessabstürze und Dateisystemfehler sind nicht umfassend real getestet. Wiederverwendung findet nicht statt. |
| BUG-054 | Teilweise definiert und getestet. Geräteframes zählen auch eingefügte Stille; Nutz- und Timelineframes zählen gelieferte Audioframes. Bei Unterlauf pausiert die Timeline, während das Gerät Stille ausgibt. Tests prüfen auch einen Teilblock, Unterlauf und EOF. Eine Cursor-/Latenz-Integration steht aus. |

## Wiedergabeintegration

Der Ringpuffer bleibt ein interner Prototyp. Prefill bereitet Blöcke vor und meldet Timeout oder Rendererfehler; Tests decken auch kurze Quellen und einen Fehler nach bereits gepufferten Blöcken ab. Die vorhandene Engine ist weiterhin Standard. Die experimentelle UI-Umschaltung, Generationen, Seek/Loop, Live-Regler und sichere VST-Invalidierung sind noch nicht umgesetzt. Ein konkretes Hindernis für die Gleichwertigkeit: BlockStretchSource.ReadFrames erlaubt derzeit ausschließlich fortlaufendes Lesen ab dem Clipanfang; beliebige Sprünge und Loops mit korrekt erhaltenem WSOLA-Zustand sind nicht implementiert. Daher wurde die Wiedergabe nicht vorschnell umgeschaltet. Kein Hardwaretest oder Wiedergabe-Langzeitlauf wurde durchgeführt.

Offline-Benchmark in einem isolierten Release-Prozess auf Windows, AMD Ryzen 5 5600G, 48 kHz Stereo: acht getrennte, nicht-sparse, synthetische Dateien mit hörbarem periodischem Signal und je 60 Minuten (zusammen 11.059.200.464 Bytes) wurden ohne Effekte in 24,623 Sekunden gemischt. Peak Working Set: 85.491.712 Bytes (81,5 MiB); die temporäre Ausgabedatei benötigte 1.382.400.058 Bytes. Die Messung umfasst den Export; die Quellgenerierung erfolgte davor. Dies ist kein Hardware- oder Echtzeittest und enthält keine VST-Effekte.

## Prüfung und Veröffentlichung

- `dotnet test tests\WellenEditor.Tests\WellenEditor.Tests.csproj --no-restore`: 217 bestanden.
- Release-Publish für win-x64 erfolgreich; bestehende NAudio-Veraltungswarnungen und eine vorhandene async-Warnung bleiben.
- `git diff --check`: vor Commit erfolgreich. Kein manueller GUI- oder Hardwaretest der neuen EXE.
- Bericht 06 wurde über Google Drive für Desktop nach `H:\Meine Ablage\Projekte` übertragen und mit der lokalen Fassung verglichen.
- Umgesetzter Code bis Commit `db11cf7`; der Bericht wird separat eingecheckt.
- Arbeitsauftrag 07 ist **nicht abgeschlossen**. Die offenen Befunde und Abnahmepunkte oben sind die Fortsetzung; ASIO, Exclusive, RF64 und Projektformat wurden nicht geändert.
