# WaveBench-Projektformat (`.wbproj`)

WaveBench Pro 0.3 speichert Mehrspurprojekte als UTF-8-JSON. Das Format ist
Version **3**. Version 2 wird weiterhin gelesen und beim nächsten Speichern migriert. Unbekannte Felder und
neuere Versionen werden absichtlich abgelehnt, damit Daten nicht unbemerkt
beim nächsten Speichern verloren gehen.

## Garantierte Felder

- `name`, `sampleRate`, `bpm`, `beatsPerBar`, `beatUnit` beschreiben das
  Projekt. Clippositionen sind ganzzahlige `startFrame`-Werte, niemals BPM-
  gerundete Sekunden.
- Version 3 ergänzt `projectId`, `portable` und `view`: Auswahl, Cursor und Viewport in 64-Bit-Frames, horizontale/vertikale Skalierung und Loop-Zustand. Quell- und Projekt-IDs bleiben über Speichervorgänge erhalten. Nicht implementierte Clip-Quelloffsets/-längen werden ausdrücklich abgelehnt statt ignoriert. Doppelte JSON-Felder werden abgelehnt.
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
sichere Regel; **Projekt portabel speichern** bietet auch in der Oberfläche den portablen Modus, der
alle Quellen in diesen Asset-Ordner kopiert. Assets werden zuerst atomar
geschrieben, anschließend ersetzt eine temporäre Manifestdatei die `.wbproj`.
Bei Fehler oder Abbruch bleibt die vorherige Manifestdatei gültig.

Beim Öffnen wird das gesamte Manifest validiert, alle Quellen werden vor dem
Ersetzen des geöffneten Projekts über Größe und SHA-256 geprüft. Fehlt oder
ändert sich eine Quelle, bietet ein Relink-Dialog die explizite Neuzuordnung byteidentischer Originale über Größe/SHA-256 an. Falsche Dateien oder Abbrechen ersetzen das aktuelle Projekt nicht. Erfolgreiches Relink bleibt bis zum Speichern als Änderung markiert. Netzwerk-/Gerätepfade, alternative Datenströme und Reparse-Points werden nicht automatisch als Quellen geöffnet. Undo/Redo wird innerhalb der laufenden Sitzung
erhalten; über einen Neustart wird es nicht persistiert.

Portables Speichern verwendet blockweise erfasste Audiosnapshots und inhaltsadressierte WAV-Assets. Unveränderte Inhalte werden wiederverwendet; keine kompletten großen WAV-Dateien werden für den Export in RAM geladen. Bei Fehler oder Abbruch werden neu erstellte, noch nicht veröffentlichte Assets entfernt. Alte Assets werden nicht aggressiv gelöscht, weil offene Dokumente/Undo darauf verweisen können. Projektpfad, Speichermodus und Dirty-State werden erst nach erfolgreichem Manifest-Commit übernommen.

## Recovery

Autosave legt getrennte portable Generationen unter `%LOCALAPPDATA%/WaveBenchPro/Recovery` an. Beim Start ohne explizite Datei sowie über **Projekt wiederherstellen** stehen Wiederherstellen, Verwerfen und Später bereit. Wiederherstellen überschreibt kein Original; der Zustand bleibt ungespeichert. Drei nicht geöffnete Generationen pro Projekt werden aufbewahrt; aktiv wiederhergestellte Generationen bleiben zusätzlich bis zum Schließen des Editors geschützt. Erst erfolgreiches manuelles Speichern oder ausdrückliches Verwerfen löst Recovery-Daten auf. Beschädigte neuere Generationen verhindern nicht die Suche nach einer älteren gültigen Generation.

## Öffnbares Beispiel

[`docs/examples/example.wbproj`](examples/example.wbproj) und seine nebenliegende
`silence.wav` bilden ein synthetisches, tatsächlich vom Loader akzeptiertes
Projekt. Beide Dateien müssen zusammenbleiben.
