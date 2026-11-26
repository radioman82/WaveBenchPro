# v0.3.0-beta.2 — draft

Windows 11 x64 portable development beta. This is an unsigned draft pending hardware acceptance, clean-machine checks and the licensing decisions in [LicenseStatus.md](LicenseStatus.md). It is not yet approved for redistribution.

## Changes

- Fixed export failing when its worker accessed WPF dialog controls; parameters are now captured on the UI thread.
- Export destinations default to the source folder, or Documents for unsaved audio, rather than an arbitrary launcher working directory.
- Supersedes the unapproved beta.1 draft without moving its existing tag.

- Classic waveform editing and a multitrack sequencer with track inserts, mixer controls and selection loops.
- Optional prepared playback with bounded buffering, owned disk-cache lifetimes and corrected seek/loop handling.
- Dedicated recording/playback IO workers and additional cleanup regressions.
- Bilingual readmes, real screenshots using synthetic audio, a user guide and architecture documentation.

## Install and verify

When an approved asset becomes available, download the win-x64 ZIP and SHA256SUMS.txt from this release. Compare the archive's SHA-256, extract the entire archive into a writable folder, and start WaveBenchPro.exe. Preserve the native codec DLLs, documentation and notices. No .NET SDK is required for the self-contained application. BUILD.json identifies the source commit and SDK.

## Known limits

The previous playback engine remains the default; enable prepared playback explicitly under Optionen. Preparation can take minutes and rerenders all occupied tracks after clip/effect changes. Mixer reaction includes queued audio. Plugin compatibility and long-session hardware reliability are not guaranteed.

Projects cannot yet be saved/reopened. WAV/RIFF size limits apply; RF64, ASIO and WASAPI Exclusive are absent. Native VST3 plugins run in-process without crash isolation. No commercial samples or third-party plugins are included.

See [UserGuide.md](UserGuide.md), [CHANGELOG.md](../CHANGELOG.md), and [LicenseStatus.md](LicenseStatus.md). Report reproducible issues through the repository's Issues page, omitting private recordings and paths.
