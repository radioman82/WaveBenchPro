# Third-party notices

WaveBench Pro's own code is licensed under the MIT License in [LICENSE](LICENSE). Third-party software has separate licenses.

The beta.3 publish profile excludes the native compressed-audio codecs listed below and supports WAV files. The codec entries document dependencies of the ordinary development build and earlier packages.

| Component | Package / origin | Notice |
|---|---|---|
| NAudio Core, MIDI, WinMM, WASAPI and SoundFile | NAudio 3.3.0 family | MIT notice from the exact package source commit in licenses/NAudio-MIT.txt |
| ASIO driver integration | NAudio.Asio 3.3.0 | MIT in licenses/NAudio-MIT.txt. Uses the machine's installed driver. |
| VST3 hosting | NAudio.Vst3 3.3.0 | MIT in licenses/NAudio-MIT.txt. VST plugins carry their own terms. |
| NAudio.Lame | 2.1.0, Corey Murtagh | licenses/NAudio-Lame-MIT.txt; native LAME is separate |
| libsndfile | org.sndfile.runtime.win-x64 1.2.2 | licenses/libsndfile-COPYING.txt, LGPL texts |
| libmp3lame | Runtime package and NAudio.Lame native DLLs | LGPL-family terms; exact build/source obligations pending review |
| mpg123, out123, syn123 | Runtime package | licenses/mpg123-COPYING.txt and LGPL text |
| FLAC | Runtime package | licenses/FLAC-COPYING.Xiph.txt |
| Ogg / Vorbis | Runtime package | licenses/libogg-COPYING.txt, libvorbis-COPYING.txt |
| Opus | Runtime package | licenses/Opus-COPYING.txt |
| .NET / WPF runtime | Microsoft.NETCore.App.Runtime.win-x64 and Microsoft.WindowsDesktop.App.Runtime.win-x64 9.0.23 | Supplied runtime LICENSE and THIRD-PARTY-NOTICES in licenses/ |
| System.Numerics.Tensors | 9.0.0, source revision `9d5a6a9aa463d6d10b0b0ba6d5982cc82f363dc3` | MIT. The package license and notices match the two Microsoft.NETCore.App.Runtime notice files byte for byte. |

See [license status](docs/LicenseStatus.md), [native binary inventory](docs/NativeDependencies.json) and [license source URLs](licenses/SOURCES.json). Native codec terms are not superseded by an aggregate NuGet package's license label.

VST is a registered trademark of Steinberg Media Technologies GmbH. WaveBench Pro hosts VST3 through NAudio; no Steinberg endorsement or third-party plugin license is implied.

This draft includes no installed VST plugins or user audio material. Do not redistribute the draft until the documented acceptance and licensing gates are resolved.
