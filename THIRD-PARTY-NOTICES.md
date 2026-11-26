# Third-party notices

WaveBench Pro uses third-party software with separate licenses. The application owner has not selected a license for the project's own code.

| Component | Package / origin | Notice |
|---|---|---|
| NAudio Core, MIDI, WinMM, WASAPI, ASIO, SoundFile and VST3 | NAudio 3.3.0 family | MIT notice from the exact package source commit; see licenses/NAudio-MIT.txt and docs/LicenseStatus.md |
| NAudio.Lame | 2.1.0, Corey Murtagh | licenses/NAudio-Lame-MIT.txt; native LAME is separate |
| libsndfile | org.sndfile.runtime.win-x64 1.2.2 | licenses/libsndfile-COPYING.txt, LGPL texts |
| libmp3lame | Runtime package and NAudio.Lame native DLLs | LGPL-family terms; exact build/source obligations pending review |
| mpg123, out123, syn123 | Runtime package | licenses/mpg123-COPYING.txt and LGPL text |
| FLAC | Runtime package | licenses/FLAC-COPYING.Xiph.txt |
| Ogg / Vorbis | Runtime package | licenses/libogg-COPYING.txt, libvorbis-COPYING.txt |
| Opus | Runtime package | licenses/Opus-COPYING.txt |
| .NET / WPF runtime | Self-contained runtime packs | Supplied runtime LICENSE and THIRD-PARTY-NOTICES in licenses/ |

See [license status](docs/LicenseStatus.md), [native binary inventory](docs/NativeDependencies.json) and [license source URLs](licenses/SOURCES.json). Native codec terms are not superseded by an aggregate NuGet package's license label.

VST is a registered trademark of Steinberg Media Technologies GmbH. WaveBench Pro hosts VST3 through NAudio; no Steinberg endorsement or third-party plugin license is implied.

This draft includes no installed VST plugins or user audio material. Do not redistribute the draft until the documented acceptance and licensing gates are resolved.
