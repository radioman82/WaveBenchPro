# Ordnerstruktur und Aufräumen

Bestandsaufnahme am 24.11.2026. Es wurden bei dieser Prüfung keine lokalen Dateien gelöscht. Größen sind gerundet und ändern sich beim nächsten Build.

| Bestandteil | Zweck | Empfehlung |
| --- | --- | --- |
| `*.cs`, `*.xaml`, `WellenEditor.csproj`, `app.manifest`, `app.ico` | Quellcode, Oberfläche und Anwendungskonfiguration | Behalten und in Git sichern. Die vielen C#-Dateien sind aufgeteilte Programmbausteine. |
| `tests/WellenEditor.Tests/*.cs` und `*.csproj` | Automatische Prüfungen für Audioquellen, Verarbeitung, Mehrspur und Pegel | Behalten und in Git sichern. |
| `.git` | Lokale Versionshistorie und Verbindung zu GitHub | Behalten. |
| `README.md`, `.gitignore`, `docs`, `WaveBenchPro_Ausbauplan.pdf` | Dokumentation, Ausschlussregeln und älterer Ausbauplan | Behalten. Der PDF-Plan kann vom bereits erreichten Stand abweichen. |
| `START_WaveBenchPro.cmd` | Startet die aktuelle Entwicklungs-EXE im Release-Buildordner | Behalten. |
| `build_img.cmd`, `build_img.ps1` | Alternative Helfer zum Erzeugen einer portablen EXE im Ordner `Release` | Kleine Build-Skripte; keine großen Altdateien. Behalten. |
| `.tooling` – ca. 728 MB | Lokales .NET-SDK zum Bauen und Testen | Derzeit behalten. Erst entfernbar, wenn ein anderes .NET-9-SDK eingerichtet und der Build damit geprüft ist. Nicht in Git. |
| `bin` – ca. 284 MB vor neuem Publish | Aktuelle EXE, erforderliche DLLs und weitere Build-Ausgaben | Den genutzten Release-Ordner behalten. Debug-Ausgaben können bei geschlossener Anwendung gelöscht und neu gebaut werden. Nicht einzelne DLLs aus dem laufenden Programmordner entfernen. Nicht in Git. |
| `obj` – ca. 4,5 MB | Generierte Zwischenprodukte und NuGet-Restore-Daten | Bei beendetem Build entfernbar. Der nächste Restore/Build erzeugt sie erneut. Nicht in Git. |
| `tests/**/bin`, `tests/**/obj`, `TestResults` | Generierte Testprogramme und Ergebnisse | Bei beendeten Tests entfernbar und erneut erzeugbar. Test-Quellcode behalten. Nicht in Git. |
| `playback-verification`, `portable-playback-fix`, `portable-rulers`, `portable-zoom-scales`, `Release`, `Release-SkalenZoom` – zusammen ca. 1.052 MB | Ältere eigenständige EXE-Ausgaben | Nach Sicherung und Prüfung der aktuellen portablen Version entfernbar, sofern keine alte Version zur Fehlersuche gewünscht ist. Nicht in Git. |
| `portable-wasapi` – ca. 207 MB | Ältere EXE plus `DHDL Demo.wav` | Alte EXE ist entfernbar; die Demo ist die bisherige Referenz für Wiedergabetests. Vor Entfernen des ganzen Ordners die WAV separat lokal sichern. Nicht in Git. |
| `samples` – ca. 28,8 MB | Vom Nutzer bereitgestellte musikalische Testsamples | Behalten. Nicht Teil der Anwendung und nicht in Git hochladen. |
| `DHDL Demo_export.wav`, `smoke_test.wav`, `smoke_test_export.wav`, `test.mp3` und Exporte wie `neu_export.flac`/`.ogg` im Buildordner | Lokale Test- und Exportdateien | Zum Programmstart nicht nötig. Nur löschen, wenn die Aufnahmen/Exporte nicht mehr gebraucht werden. Nicht in Git. |

## Sinnvolle Reihenfolge

1. Quellcode, Tests und Dokumentation auf GitHub sichern.
2. Eine aktuelle portable EXE bauen, starten und separat als Release-Asset sichern.
3. Eigene Audiodateien und die Referenz-WAV behalten oder außerhalb des Projektordners sichern.
4. Danach ältere EXE-Ausgabeordner und generierte Test-/Debug-Ausgaben entfernen, falls gewünscht.

GitHub ersetzt damit die Sicherung des Entwicklungsstands und der veröffentlichten EXE. Für eigene Audiodateien und ungespeicherte Mehrspur-Zusammenstellungen ist weiterhin eine separate Sicherung nötig.
