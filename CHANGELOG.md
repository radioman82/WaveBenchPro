# Changelog

## Unreleased — hardware backends

- Unified NAudio 3.3.0 builder-based WASAPI playback/capture, retaining Shared default and both multitrack engines.
- Optional explicit-format Exclusive playback; installed ASIO drivers with independent playback/recording, channel maps, driver buffer and control panel.
- Persisted input/output identity and mode, device-loss reselection, actual hardware status and partial-recording recovery.
- Preallocated capture packets, read-ahead for file/live-plugin sources, failed native-retirement ownership and playback wait-handle cleanup.
- Corrected persisted WAVEFORMATEXTENSIBLE decoding; expanded deterministic regressions and separate hardware measurements.
- Avoided NAudio's per-packet ASIO capture event allocation through its public raw buffer API; preserved partial recordings on failed native retirement.
- Kept a valid waveform viewport when undo shortens a recording beyond the previous visible window.
- Exact NAudio MIT source notice and native codec provenance inventory. Redistribution and clean-host gates remain open.

No new version/tag/release approval is implied; the existing beta.2 draft assets remain unchanged.

## 0.3.0-beta.2 — unreleased draft

- Fixed file export reading WPF dialog controls from a background thread.
- Default and relative export filenames now use the source folder (Documents for unsaved audio), independent of the launcher's working directory.
- Retains all beta.1 features and the same acceptance/licensing gates.
- Supersedes the unapproved beta.1 draft; its tag is preserved unchanged.

## 0.3.0-beta.1 — unreleased draft

- Optional prepared multitrack playback with private disk files and bounded queues.
- Controlled seek/loop transport, generations, buffer choices and underrun/errors.
- Preserved selection bounds; corrected cleanup, stop status and view switching.
- Float headroom, length-limited inserts and block-based WAV export.
- Corrected anti-phase WSOLA correlation in both rendering paths.
- Cache ownership/orphan cleanup and dedicated playback/recording IO workers.
- Additional regressions and public bilingual documentation.

Known limits: no saved projects, RF64, ASIO, Exclusive or isolated plugins. Changes prepare all occupied tracks again. Controls include queue latency. Full acceptance, clean-machine testing and licensing remain release gates.

## Earlier snapshots

See [GitHub release notes](https://github.com/radioman82/WaveBenchPro/releases). These are development snapshots.
