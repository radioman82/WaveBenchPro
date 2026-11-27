# WaveBench-Projektformat (`.wbproj`)

WaveBench Pro 0.3 speichert Mehrspurprojekte als UTF-8-JSON. Das Format ist
Version **2**. `formatVersion` muss genau `2` sein; unbekannte Felder und
neuere Versionen werden absichtlich abgelehnt, damit Daten nicht unbemerkt
beim nächsten Speichern verloren gehen.

## Garantierte Felder

- `name`, `sampleRate`, `bpm`, `beatsPerBar`, `beatUnit` beschreiben das
  Projekt. Clippositionen sind ganzzahlige `startFrame`-Werte, niemals BPM-
  gerundete Sekunden.
- `sources` enthält stabile IDs, Pfad, Bytegröße und SHA-256. Relative Pfade
  bleiben innerhalb des Projektordners; externe Referenzen werden absolut
  gespeichert. URLs, `..`-Traversal und geänderte Quellen werden abgelehnt.
- `tracks` behält Reihenfolge, ID, Namen, Gain, Panorama, Stumm/Solo und Clips
  (Quell-ID, Startframe, Stretch-Faktor). Mixer- und Clipbearbeitungen werden
  damit nichtdestruktiv wiederhergestellt.
- Jede Effektkette enthält die nativen Einstellungen sowie, bei VST3, nur
  Klassen-ID, Hersteller/Name und höchstens 1 MiB Base64-State. Ein VST wird
  ausschließlich gegen den lokal gescannten Katalog aufgelöst; ein Projekt kann
  weder Plugin-Pfade noch Downloads oder Code ausführen. Fehlende Plugins
  bleiben deaktivierte Platzhalter mit erhaltenem State.

Grenzen: 4 MiB Manifest, 10.000 Quellen, 1.000 Spuren, 100.000 Clips,
128 Effekte pro Spur, Sample-Rate 8–384 kHz und BPM 30–300. IDs, Referenzen,
Zahlen und Base64 werden beim Laden validiert.

## Speichern und Quellen

**Projekt speichern** schreibt Referenzen auf unveränderte Originaldateien.
Bearbeitete, aufgenommene oder unbenannte Quellen werden als 32-bit-WAV in
`<Projektname>.assets` abgelegt. **Projekt speichern unter** verwendet dieselbe
sichere Regel; die interne API kann außerdem den portablen Modus benutzen, der
alle Quellen in diesen Asset-Ordner kopiert. Assets werden zuerst atomar
geschrieben, anschließend ersetzt eine temporäre Manifestdatei die `.wbproj`.
Bei Fehler oder Abbruch bleibt die vorherige Manifestdatei gültig.

Beim Öffnen wird das gesamte Manifest validiert, alle Quellen werden vor dem
Ersetzen des geöffneten Projekts über Größe und SHA-256 geprüft. Fehlt oder
ändert sich eine Quelle, bricht das Öffnen mit deren Namen ab und das bisherige
Projekt bleibt unverändert. Undo/Redo wird innerhalb der laufenden Sitzung
erhalten; über einen Neustart wird es nicht persistiert.

## Öffnbares Beispiel

[`docs/examples/example.wbproj`](examples/example.wbproj) und seine nebenliegende
`silence.wav` bilden ein synthetisches, tatsächlich vom Loader akzeptiertes
Projekt. Beide Dateien müssen zusammenbleiben.
