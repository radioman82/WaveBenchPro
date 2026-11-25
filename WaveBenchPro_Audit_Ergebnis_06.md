# WaveBench Pro – Arbeitsauftrag 06: Zwischenstand

**Datum:** 25.11.2026  
**Basis:** `90ab934cf55b1eb17c6c804ab246877bcead1d66`  
**Etappe A, Teilimplementierung:** Commits `f85d605` und `3a28abf`  
**Branches:** `feature/v0.3`, `main`.

## Etappe A – teilweise umgesetzt

`BlockMixdownRenderer.cs` mischt Mono- und Stereoquellen blockweise. Er berücksichtigt Clippositionen, Überlappungen, Lücken, Stumm/Solo, Gain/Pan und Inserts in Reihenfolge. Jede Rendererinstanz besitzt eigene Effektzustände; float-Headroom bleibt erhalten. Streaming-WAVs verwenden die bestehende segmentierte Quelle und werden nicht vollständig materialisiert. Der bisherige begrenzte Mixdown bleibt separat erhalten.

Die neue Schaltfläche „Mixdown als WAV“ exportiert Float32-Stereo in eine temporäre Datei im Zielordner und ersetzt das Ziel erst nach erfolgreichem Abschluss und Schließen. Fortschritt und Abbruch sind im Fenster vorhanden. Eine geöffnete Quelldatei darf nicht überschrieben werden; vorhandene Ziele werden über Windows-Dateiidentität auf Hardlinks und aufgelöste symbolische Links geprüft. Ein Hardlink-Regressionstest ist enthalten; der Symlink-Fall wurde nicht separat getestet. RIFF-Größe wird vorab geprüft. Dies ist kein vollständiger Nachweis der atomaren Veröffentlichung unter allen Dateisystem- und Fehlerbedingungen; eine nach der Identitätsprüfung ausgetauschte Datei bleibt ein Konkurrenzrisiko.

Tests in `BlockMixdownTests.cs` vergleichen den bisherigen und den neuen Mixer mit Sinus, Gegenphase, Überlappung, Lücke, Gain/Pan und einem Hochpass für alle 16 Kombinationen der Blockgrößen 256, 1024, 4096 und 8192 mit 44,1/48/96/192 kHz. In diesen Vergleichen betrugen maximale und RMS-Abweichung jeweils 0; die Testtoleranz beträgt 1e-6. Weitere 16 Kombinationen prüfen Impulse und Stille an Blockgrenzen. Zusätzliche Tests prüfen Stumm/Solo, Streaming-WAV nach Schnitt, Float-WAV einschließlich Headroom, Abbruch nach Beginn, Quellpfad, gesperrtes Ziel, simulierten Schreibfehler und explizite VST-Ablehnung. 1e-6 ist eine numerische Testtoleranz und keine Beurteilung der Klangqualität.

**Offen für A:** Weitere Quell-/Zielfehler, tatsächlicher Datenträger-Voll-Test und Hardwarewiedergabe sind nicht abgenommen. Die unten dokumentierten Messungen beziehen sich auf synthetische Stille, eine konkrete Maschine und einen Testlauf; sie sind keine allgemeine RAM-Garantie. Die bestehenden Grenzen des bisherigen Renderers bleiben bestehen.

### Isolierte Messungen

Windows, AMD Ryzen 5 5600G, .NET 9, Release, getrennte Prozesse, 48 kHz Stereo, acht Spuren, keine Effekte. `benchmarks/MixdownBench` schreibt jeweils eine WAV und entfernt die eigene Testdatei anschließend. Alter und neuer Renderer verwendeten für den kurzen Vergleich dieselben kurzen stillen Clips am Anfang und Ende der 60-Sekunden-Timeline:

| Fall | Zeit | Peak Working Set | Ausgabe / temporärer WAV-Bedarf |
|---|---:|---:|---:|
| Bisheriger Renderer, 60 s, dünn belegt | 156 ms | 50,4 MiB | 23.040.058 Bytes |
| Blockrenderer, 60 s, dünn belegt | 113 ms | 27,8 MiB | 23.040.058 Bytes |
| Blockrenderer, 60 min, dünn belegt | 3,5 s | 30,0 MiB | 1.382.400.058 Bytes |
| Blockrenderer, 8 durchgehende Spuren à 60 min | 10,4 s | 40,5 MiB | 1.382.400.058 Bytes |

Für den letzten Fall wurde eine gemeinsame synthetische Float32-WAV voller Stille mit 60 Minuten logischer Länge als NTFS-Sparse-Datei erzeugt und von acht Spuren gelesen. Sie wird zusätzlich zur Ausgabe benötigt; ihr logischer Umfang beträgt rund 1,38 GB, die tatsächliche physische Belegung wurde nicht gemessen. Peak Working Set bezieht sich auf den gesamten Benchmarkprozess einschließlich Quellenaufbau und Hintergrundcache, Laufzeit auf den Mixdown ab Start bis Speichern. Das Ziel für diese Messung war unter 256 MiB; der gemessene Peak erfüllt es für diesen konkreten Fall. Acht getrennte Dateien, reale Musik, Plugins, 32 Spuren und anderer Datenträgerdruck wurden nicht gemessen.

## Etappe B – Offline-Resampling und Stretch

Sampleratenumrechnung und Time-Stretch arbeiten im Offline-Mixdown blockweise. Der Resampler bestimmt absolute Positionen und liest den notwendigen Filtervorlauf; der Stretch verarbeitet Fenster fortlaufend mit gemeinsamer Stereo-Ausrichtung. Kurze Clips behalten die bisherige Varispeed-Behandlung. Ein Vergleich über 128 Kombinationen aus vier Projekt- und Quellsampleraten, vier Blockgrößen und zwei Stretchfaktoren ergab maximal und RMS jeweils 0 Abweichung gegenüber dem bestehenden Renderer. Zusätzlich wurden geschnittene Streaming-WAVs, unterschiedliche Clips und zufällige Blockgrößen geprüft. Seek und Loop sind für den neuen Stretch-Pfad nicht implementiert; er ist für fortlaufenden Offline-Export ausgelegt.

## Etappe C – VST-Zwischendateien teilweise umgesetzt

VST-Spuren werden pro Export blockweise in private temporäre Float-WAVs gerendert, anschließend gemischt und beim Abschluss entfernt. Native und VST-Effekte bleiben in Reihenfolge; Fehler und kurze Ausgaben brechen ab. Ein Cache-Limit von 16 GiB pro Export und RIFF-Grenzen werden geprüft. Getrennte private Verzeichnisse und exklusive Marker schützen gleichzeitig aktive Exporte; erkennbare verwaiste Dateien werden bei einer späteren Sitzung bereinigt. Es gibt bewusst keine Wiederverwendung zwischen Exporten, daher noch keinen Cache-Schlüssel, keine generationsbasierte Invalidierung und kein gemeinsames Diskbudget. Bei den installierten Kilohearts Gain und Trance Gate war die maximale Abweichung zum alten Renderer in kurzen Einmaltests 0; Plugin-Latenz, State und weitere Drittanbieter-Plugins bleiben gesondert zu prüfen.

## Etappe D – Wiedergabepuffer als Prototyp

Ein separater Ringpuffer liest vorbereitete Blöcke ohne Dateizugriff im Callback, zählt Unterläufe und gibt bei leerem Puffer Stille aus. Tests decken blockierten Producer, Pufferende, Abbruch bei vollem Puffer und Producerfehler ab. Der Prototyp ist nicht an die Benutzeroberfläche angeschlossen; Live-Regler, Seek, Loop, Geräteverhalten und sichtbare Unterlaufanzeige fehlen. Die bestehende Wiedergabe bleibt aktiv. C und D sind nicht vollständig abgenommen.

Zusätzliche isolierte Release-Messungen mit acht synthetischen stillen Spuren: 10 s Sampleratenumrechnung 48→44,1 kHz: 1,30 s und 32,5 MiB Peak; 10 s mit Stretch 1,05: 0,25 s und 30,2 MiB; 60 Minuten mit Stretch 1,05: 50,1 s und 40,7 MiB Peak, Ausgabe 1.451.520.058 Bytes. Diese Einzelmessungen ersetzen keine Tests mit realem Audiomaterial und Plugins.

## Prüfungen

- `dotnet test tests\WellenEditor.Tests\WellenEditor.Tests.csproj --no-restore`: **203 bestanden, 0 fehlgeschlagen**.
- `dotnet build WellenEditor.csproj -c Release --no-restore`: erfolgreich.
- Release-Publish für `win-x64`: erfolgreich; EXE unter `D:\develop\WellenEditor\bin\Release\net9.0-windows\win-x64\WaveBenchPro.exe`.
- `git diff --check`: erfolgreich vor Commit.
- Veröffentlichtes Programm geöffnet, `SLTN_126_A_Piano_Loop_07.wav` geladen, in eine Spur gezogen und WAV über die Oberfläche exportiert; Erfolgsmeldung und erzeugte Datei geprüft. Die eigens erstellte Testausgabe wurde entfernt. Keine Hardwarewiedergabe und kein realer Datenträger-Voll-Test.
- BUG-047-Regression ist in der vollständigen Testsuite enthalten. Undo, Auswahl-Schleife und längentreue Effekte wurden nicht umgebaut.
