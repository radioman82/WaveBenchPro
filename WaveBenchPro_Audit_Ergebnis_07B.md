# WaveBench Pro – Ergebnis 07B (Zwischenstand / Pause)

Stand: 25.11.2026. Auf ausdrücklichen Wunsch des Benutzers pausiert.
**07B ist noch nicht vollständig abgenommen.** Ausgangspunkt e064225, gespeicherte Codeänderungen bis a623262 (einschließlich ce8f586).

## Umgesetzt

- Experimentelle Umschaltung unter Optionen: „Mehrspur: neue Wiedergabe (experimentell)“. Bisherige Engine bleibt Standard und manuell wählbarer Rückfall.
- Private dateibasierte Spurvorbereitung mit dem vorhandenen BlockMixdownRenderer. Jede Spur wird einschließlich Resampling, WSOLA und Inserts kontinuierlich vorbereitet. Sprünge/Loops lesen die vorbereiteten Samples; kein erneutes Rendern ab Clipanfang bei jedem Seek.
- Konfigurierbarer Ringpuffer (4/8/16 Blöcke à 1024 Frames), Prefill, EOF, Play/Stop und Transportgenerationen. Veraltete Veröffentlichungen werden verworfen; Positionswechsel stoppen den vorherigen Producer vor dem neuen.
- Gain/Pan/Mute/Solo werden im Producer angewandt und benötigen keine erneute Effektvorbereitung. Änderungen wirken nach bereits gefüllten Puffern. Clipänderungen starten eine neue Vorbereitung; Effekte werden nach Schließen des FX-Dialogs neu vorbereitet. Snapshots kopieren Clipwerte, Effektfolge und VST-State.
- Cursor nutzt die tatsächlich an das Gerät gelieferten Frames und eine zweisekündige Positionshistorie, einschließlich Loopwechseln und Unterlaufstille. Die Ausgangslatenz wird weiterhin mit den angeforderten 50 ms angenähert; keine Messung der tatsächlichen akustischen Latenz.
- Der Callback kopiert vorgerenderte Blöcke und signalisiert freie Plätze. Keine Quelldateizugriffe oder Pluginaufrufe im Callback.
- Private Vorbereitungsdateien nutzen das gemeinsame 16-GiB-Leasebudget. Kein Cache-Reuse: Änderungen von Quelle, Position, Tempo, Rate, Effektfolge, Aktivierung, Plugin oder State erhalten neue private Dateien. Aktive Renderer halten die Dateien am Leben.
- Zusätzliche Sicherung beim Stoppen: Eine neue UI-Vorbereitung wartet auf die vorherige Finalisierung; bei Timeout wird kein neuer Producer gestartet. Session-Cleanup erfolgt außerhalb des Audio-Callbacks.

## Befunde

| ID | Ergebnis und Grenzen |
|---|---|
| BUG-048 | Zielidentitätsprüfung und Hardlink-Regressionsschutz aus 07 erhalten. Erneuter Symlink-Versuch scheiterte an fehlendem Windows-Recht; Schutztest damit nicht ausführbar. Das verbleibende Pfad-TOCTOU-Rennen bleibt dokumentiert. |
| BUG-049 | Exportlänge bleibt wie bisher das Ende der hörbaren Spuren. Die experimentelle Wiedergabe verwendet bewusst die gesamte Projekttimeline, damit lange stumme Spuren live eingeschaltet werden können. Vorbereitete Spurdateien bleiben längentreu. |
| BUG-050 | Intervallindex erhalten. Release-Vergleich mit 20.000 Clips und 20.000 Blöcken: Index 15,8 ms, lineare Suche 225,3 ms; gleiche Trefferzahl. Synthetischer Einzellauf, kein allgemeiner Geschwindigkeitsfaktor. |
| BUG-051 | Cleanup wurde weiter abgesichert: Fehler beim Renderer-Dispose verhindern nicht mehr den Versuch, die temporäre Exportdatei zu entfernen. VST-Ressourcen werden auch nach einzelnen Dispose-Fehlern vollständig durchlaufen; Primärfehler und Cleanup-Details bleiben erhalten. |
| BUG-052 | Begrenztes asynchrones Stoppen, kooperativer Abbruch und Besitz durch den Worker erhalten. Blockierende native Plugin- oder synchrone Dateiaufrufe können weiterhin nicht zwangsweise unterbrochen werden. |
| BUG-053 | Gemeinsames Budget und aktive Leases erhalten; auch die vorbereiteten Wiedergabespuren werden reserviert. Kein unsicherer Reuse. Vorhandene Konkurrenz-/Orphan-Tests laufen weiter; echter Prozessabsturz und volle physische Disk nicht neu geprüft. |
| BUG-054 | Geräte-, Nutz- und Timelinezählung getrennt; bei Unterlauf bleibt die Quellposition stehen. Die neue Positionshistorie führt Stille und Loopwechsel mit. End-to-End-Cursorabnahme noch offen. |
| BUG-055 | Bestätigt. Kanalweise Produkte/Energien ersetzen die vorherige L/R-Summe in BEIDEN WSOLA-Pfaden. Dadurch verändert sich die frühere Stereo-Kandidatensuche absichtlich; Offline- und Streamingpfad bleiben untereinander gleich. Gegenphase-Test prüft gleiche linke Ausgabe bei gleichphasigem und gegenphasigem Eingang. |
| BUG-056 | Sleep-Polling bei vollem Ring durch Producer-Warten auf ein Signal ersetzt. Callback wartet nicht auf den Producer. CPU-Messung siehe unten; kein kontrollierter CPU-Vorher/Nachher-Vergleich. |
| BUG-057 | CancelAsync ersetzt synchrone Cancellation unter dem Lifetime-Lock. CTS lebt bis Worker und Cancellation-Finalisierung beendet sind. Regression injiziert blockierenden Cancellation-Callback und prüft Timeout sowie spätere Freigabe. |
| BUG-058 | Fehler nach Prefill sowie Render- und Dispose-Fehler bleiben sichtbar. UI prüft WorkerError im Timer und beim Geräte-Stopp statt Fehler als reguläres EOF zu behandeln. Neuer deterministischer Test erhält beide Fehler auch nach Leeren des Puffers. |

## Geprüft

- Vollständiger Debug-Testlauf: **227 bestanden**, keine übersprungen. Ausgangsbestand 217 plus vorhandene und neue 07B-Regressionen.
- Neue Tests: 100 Transportneustarts, konkurrierende Generationen, Seek/Loop mit 256/1024/4096/8192 Frames und 44,1/48/96/192-kHz-Kombinationen, unterschiedliche Stretchfaktoren, aktive Session-Leases, Live-Mixerregler und Gegenphase.
- Musikvergleich: samples/SLTN_126_A_Piano_Loop_07.wav, 44,1 kHz, Stretch 1,25. Erwartet und tatsächlich 420.000 Frames; max/RMS-Abweichung zum kontinuierlichen Referenzrender jeweils 0.
- Installiertes VST **TDR Kotelnikov**: State 1.328 Bytes, 22 Parameter, maximaler Parameterfehler nach State-Roundtrip 0. Impulspeak ohne Kompensation bei Frame 184, mit Kompensation bei Frame 0; gemeldete Latenz 184 Samples, Fehler 0 Frames. Gain-vor-VST: Referenz gegen vorbereitete Spur maximaler Fehler 0, 4.096 Frames. Deaktivierter VST: Bypassfehler 0.
- Echter WASAPI-Shared-Test auf „Lautsprecher (BEHRINGER USB WDM AUDIO 2.8.40)“, acht getrennte synthetische Viersekundenspuren in Schleife, 48 kHz, 8 × 1024 Frames, angeforderte Gerätelatenz 50 ms. Gemessene Laufzeit 65,34 s, Prozess-CPU 0,562 s, Peak Working Set 66.977.792 Bytes (63,9 MiB), 2.100 Callbacks, maximale Callback-Wandzeit 1.491,90 µs, Callback-Allokationen 0 Bytes, Pufferunterläufe 0.
- Anschließend **100 echte Geräte-Start/Stop-Zyklen** mit Positionswechseln: kein Workerfehler, insgesamt 0 Pufferunterläufe. Kein Test von 100 vollständigen GUI-Bedienfolgen.
- Vorheriger kurzer Hardwarelauf: 16,47 s, 0 Unterläufe, 0 Callback-Allokationen, aber maximale Callback-Wandzeit 24.358,20 µs. Dieser Ausreißer wird nicht verschwiegen; keine harte Echtzeitgarantie.
- Die Hardwaremessungen verwenden den isolierten Release-Benchmark mit Messwrapper, nicht die komplette GUI samt Metering. Ein Unterlaufzähler erfasst keine ungemeldeten Dropouts des Treibers.
- Veröffentlichte EXE gestartet; neue Menüoption sichtbar. Vollständige Import-/Playback-Bedienprüfung abgebrochen, weil Computer Use beim modalen Dateidialog widersprüchliche Fokus-/Elementdaten meldete. Testfenster anschließend geschlossen.
- Release-Build des Benchmarkprojekts und win-x64 Release-Publish erfolgreich. EXE: D:\develop\WellenEditor\bin\Release\net9.0-windows\win-x64\WaveBenchPro.exe. Vorhandene NAudio-Veraltungswarnungen und eine async-Warnung bleiben. git diff --check erfolgreich.

## Semantik und verbleibende Abnahme

Die vorbereiteten Dateien enthalten den kontinuierlichen DSP-Verlauf ab Projektanfang. Ein Seek beginnt an genau der entsprechenden gerenderten Stelle, einschließlich Resampler-/WSOLA-Vorgeschichte. Ein Loop wiederholt die ausgewählte Ausgabe; er führt die Effektzustände nicht über den Looprand weiter. Mute hält die vorbereitete DSP-Timeline nicht an. Harte Auswahlgrenzen können bei nicht passenden Samplewerten weiterhin klicken; es wird kein automatischer Crossfade hinzugefügt.

Große Projekte benötigen nun Vorbereitungszeit und Disk statt eines vollständigen RAM-Puffers. RIFF-Limit je Spur und globales 16-GiB-Budget gelten; bei Fehler kein stiller Effekt-Bypass. Abbruch kann einen hängenden nativen Pluginaufruf ohne Prozessisolierung nicht beenden.

Noch offen: 60-Minuten-Hardwarelauf, optional 32 Spuren, Gerätewechsel während Wiedergabe, Daueraufnahme mit Dateivergleich, tatsächliche akustische Latenz, vollständige GUI-Play/Stop/Seek/Loop-/Fenster-/Tab-Abnahme, ausgedehnte Diskfehler-/Crash-Konkurrenzprüfung und weitere installierte VSTs. 65 Sekunden sind KEIN 60-Minuten-Nachweis.

## Fortsetzung morgen

1. Git-Stand und diesen Bericht lesen; keine bereits umgesetzten Teile neu anfangen.
2. Die zuletzt ergänzte Session-Finalisierung und UI-Stop-Wartekette gezielt prüfen. Der letzte volle 227-Testlauf enthält diese Änderungen. Hardwarewerte oben entstanden unmittelbar davor; nach dieser Änderung den kurzen Hardwarelauf wiederholen.
3. Benchmark neu bauen und Diagnose symlink-check erneut starten: Catch für IOException wurde nach dem fehlenden Recht ergänzt, aber noch nicht erneut ausgeführt. Musik-/VST-Messwerte oben stammen aus erfolgreichen Durchläufen.
4. GUI-Import und sichtbaren Cursor mit DHDL Demo.wav prüfen; experimentelle Engine einschließlich Loop, Live-Mixer und Effektänderung abnehmen.
5. Offene Abnahme durchführen oder ausdrücklich offen kennzeichnen; anschließend Bericht, GitHub und Drive aktualisieren. Auftrag 08–12 nicht automatisch beginnen.

Keine Hardwaremigration, ASIO-, Exclusive-, RF64- oder Projektformatumstellung vorgenommen.
