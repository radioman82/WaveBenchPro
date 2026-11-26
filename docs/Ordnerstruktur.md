# Repository layout

| Path | Purpose |
|---|---|
| Root C# / XAML | WPF application, sources, DSP, rendering and transport |
| WellenEditor.csproj | Windows x64 application and package references |
| tests/WellenEditor.Tests | Automated audio/lifecycle regression tests |
| benchmarks/MixdownBench | Optional diagnostics; hardware modes play audio |
| docs / licenses | Public documentation and third-party license texts |
| scripts | Portable release packaging |
| START_WaveBenchPro.cmd | Starts the developer Release output |
| build_img.cmd / build_img.ps1 | Older alternative publish helpers |
| .tooling | Optional local tools/SDK, ignored |
| bin / obj / artifacts / TestResults | Generated outputs, ignored |
| samples / local-audio | Private local material, ignored |

Sources remain at repository root. Build outputs can be regenerated when the app/build tools are stopped. Do not remove individual runtime DLLs from a package you intend to run.

Audio and third-party plugins are not part of the repository or release package. Back up recordings and sources separately. Unsaved multitrack arrangements are not preserved by Git or application shutdown.
