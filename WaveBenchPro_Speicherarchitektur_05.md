# Entwurf: Mehrspur mit begrenztem Speicher

Stand 25.11.2026. Dieser Entwurf ist keine Freigabe zur Implementierung. Die bisherigen Schutzgrenzen bleiben bis zum Nachweis eines funktionierenden Ersatzes bestehen.

## Ziel und Datenfluss

Quellen liefern adressierte Frames in begrenzten Blöcken. Jeder Clip bildet die Projektposition auf die Quelle ab; überlappende Clips werden blockweise pro Spur summiert, danach laufen Inserts in sichtbarer Reihenfolge. Der Mixer summiert die Spuren in Float32 mit Headroom. Wiedergabe und Export verwenden denselben Renderkern, aber getrennte Zustandsinstanzen. Ein Hintergrundpuffer versorgt die Echtzeitwiedergabe; bei Unterlauf wird ausdrücklich gemeldet, dass die Wiedergabe nicht rechtzeitig vorbereitet war. Der Audio-Callback liest ausschließlich vorbereitete Blöcke.

## Resampling und Time-Stretch

Der Resampler erhält absolute Quellposition, gebrochenen Phasenrest und Filterhistorie über Blockgrenzen hinweg. Er benötigt bei zufälligem Einstieg einen definierten Vorlauf; dessen Länge und Anfangs-/Endbehandlung müssen gegen den bestehenden Ganzdatei-Renderer verglichen werden. Time-Stretch benötigt überlappende Fenster, Suchbereich und gemeinsame Stereo-Ausrichtung; Zustände dürfen nicht bei jeder Blockgrenze neu anfangen. Seek und Loop brauchen ausdrücklich definierte Reset- oder Vorlaufregeln. Kurzclips, Stereo-Gegenphase und Impulse sind eigene Prüfungen. Ein vorzeitiges Umschalten auf blockweise Verarbeitung wäre ohne diese Nachweise hörbar riskant.

## VST, Cache und Export

Ein begrenzter Cache hält vorbereitete Blöcke mit explizitem Bytebudget und Verdrängung. Eine VST-Spur wird bei Bedarf in eine temporäre Floatdatei gerendert, mit festgelegter Latenzkompensation und längentreuem Ergebnis; Effekte dürfen bei Fehler nicht übersprungen werden. Ein Cache-Schlüssel muss Quellenidentität und -änderung, Positionen, Tempo, Samplerate, Effektreihenfolge, Aktivierung, Plugin-Version und vollständigen Parameterzustand enthalten. Änderungen invalidieren betroffene Ergebnisse. Jede Renderaufgabe erhält Generation und Abbruchsignal; nur die aktuelle Generation darf Dateien veröffentlichen. Referenzen schützen genutzte Cache-Dateien vor Löschung, und ein gesondertes Diskbudget begrenzt Zwischenstände. Prozessabsturz, fehlender Platz und Plugin-Absturz müssen temporäre Dateien erkennbar hinterlassen oder sicher bereinigen.

Der Export schreibt direkt blockweise in eine temporäre Datei auf demselben Zielvolume, finalisiert den Container und ersetzt das Ziel erst nach erfolgreichem Rendern und Schließen. Bei Abbruch oder Fehler bleibt das bisherige Ziel erhalten; temporäre Dateien werden nach sicherem Schließen entfernt. Fortschritt und Abbruch werden auf UI-Ebene angeboten. RF64 und Metadaten bleiben separate Aufgaben.

## Migration und Abnahme

1. Offline-Blockrenderer zunächst parallel zum bestehenden Renderer einführen; bekannte Signale, Samplepositionen, Clips, Überlappungen, Kanäle und Effektfolge vergleichen.
2. Streaming-Ausgabe mit Fehler-, Abbruch- und Tempdateitests ergänzen; alte Grenzen beibehalten.
3. Resampler und Stretch mit Zustand über Blockgrenzen und Seek/Loop testen; kleine und große Blockgrößen vergleichen.
4. VST-Zwischendateien mit Generationen, Cache-Invalidierung, Grenzwerten und Konkurrenztests ergänzen.
5. Echtzeitpuffer und Unterlaufverhalten separat testen, dann Grenzen nur nach belastbaren Messungen ersetzen.

Abnahme umfasst lange und dünn belegte Timelines, mehrere Spuren, Überlappungen, Blockgrenzen, Wiedergabe-/Mixdown-Vergleich, Abbruch, volle Datenträger und offene Handles. Peak-Arbeitssatz und Laufzeit müssen in isolierten Prozessen bei gleicher Hardware, gleichem Material, gleicher Blockgröße und gleichem Effektzustand vor und nach der Umsetzung gemessen werden. Effekt-Tails bleiben bis zu einer gesonderten Entscheidung abgeschnitten. Keine dieser Messungen ist bereits ein Nachweis für die vorgeschlagene Architektur.
