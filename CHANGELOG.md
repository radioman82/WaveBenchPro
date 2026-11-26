# Changelog

## 0.3.0-beta.2 — unreleased draft

- Fixed file export reading WPF dialog controls from a background thread.
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
