# WaveBench Pro – Arbeitsauftrag 06: Zwischenstand

**Datum:** 25.11.2026  
**Basis:** `90ab934cf55b1eb17c6c804ab246877bcead1d66`  
**Etappe A, Teilimplementierung:** Commits `f85d605` und `3a28abf`  
**Branches:** `feature/v0.3`, `main`.

## Etappe A – teilweise umgesetzt

`BlockMixdownRenderer.cs` mischt Mono- und Stereoquellen mit gleicher Samplerate und unveränderter Clipdauer blockweise. Er berücksichtigt Clippositionen, Überlappungen, Lücken, Stumm/Solo, Gain/Pan und native Inserts in Reihenfolge. Jede Rendererinstanz besitzt eigene Effektzustände; float-Headroom bleibt erhalten. Streaming-WAVs verwenden die bestehende segmentierte Quelle und werden nicht vollständig materialisiert. Andere Abtastraten, Time-Stretch und aktive VST-Effekte werden explizit zurückgewiesen. Der bisherige begrenzte Mixdown bleibt separat erhalten.

Die neue Schaltfläche „Mixdown als WAV“ exportiert Float32-Stereo in eine temporäre Datei im Zielordner und ersetzt das Ziel erst nach erfolgreichem Abschluss und Schließen. Fortschritt und Abbruch sind im Fenster vorhanden. Eine geöffnete Quelldatei darf nicht über ihren bekannten Pfad überschrieben werden; RIFF-Größe wird vorab geprüft. Dies ist noch kein vollständiger Nachweis der atomaren Veröffentlichung unter allen Dateisystem- und Fehlerbedingungen; alternative Dateinamen über Hardlinks oder symbolische Links werden nicht als identische Quelle erkannt.

Tests in `BlockMixdownTests.cs` vergleichen den bisherigen und den neuen Mixer mit Sinus, Gegenphase, Überlappung, Lücke, Gain/Pan und einem Hochpass bei Blockgrößen 256, 1024, 4096 und 8192 sowie 44,1/48/96/192 kHz. Sampleabweichungen dürfen höchstens 1e-6 betragen; ein gesonderter maximaler und RMS-Abweichungswert wurde nicht ausgegeben. Weitere Tests prüfen Stumm/Solo, Streaming-WAV nach Schnitt, Float-WAV einschließlich Headroom, Abbruch nach Beginn, Quellpfad, gesperrtes Ziel, simulierten Schreibfehler und explizite VST-Ablehnung. 1e-6 ist eine numerische Testtoleranz und keine Beurteilung der Klangqualität.

**Offen für A:** Abnahme mit Impuls- und Stillesignalen über die vollständige Kombination von Sampleraten und Blockgrößen, ausführliche Quell-/Zielfehler, UI-Bedienung und Langzeitlast. Die im Auftrag verlangten isolierten Release-Messungen von Peak Working Set, Laufzeit und temporärem Diskbedarf, insbesondere acht Spuren à 60 Minuten, wurden noch nicht durchgeführt. Ein belastbares Gesamt-RAM-Budget ist daher noch nicht belegt; die festen Blockpuffer allein beweisen keine Prozessobergrenze. Die bestehenden Grenzen des bisherigen Renderers bleiben bestehen.

## Etappen B, C und D – offen

Zustandsbehaftetes Resampling und Time-Stretch über Blockgrenzen, VST-Dateicache und Echtzeitpuffer wurden nicht implementiert. Ihre Umsetzung setzt die Abnahme von A beziehungsweise A/B voraus. Die Wiedergabe verwendet weiterhin die bisherige Engine und teilt deshalb noch keinen Renderkern mit dem neuen Offline-Export.

## Prüfungen

- `dotnet test tests\WellenEditor.Tests\WellenEditor.Tests.csproj --no-restore`: **139 bestanden, 0 fehlgeschlagen**.
- `dotnet build WellenEditor.csproj -c Release --no-restore`: erfolgreich.
- Release-Publish für `win-x64`: erfolgreich; EXE unter `D:\develop\WellenEditor\bin\Release\net9.0-windows\win-x64\WaveBenchPro.exe`.
- `git diff --check`: erfolgreich vor Commit.
- Kein manueller GUI- oder Hardwaretest der veröffentlichten EXE. Kein realer Datenträger-Voll-Test. Kein Benchmark für 8 × 60 Minuten.
- BUG-047-Regression ist in der vollständigen Testsuite enthalten. Undo, Auswahl-Schleife und längentreue Effekte wurden nicht umgebaut.
