# WaveBench Pro 0.3.0-beta.3 candidate

This unsigned Windows 11 x64 candidate offers WAV editing, WASAPI playback and recording, multitrack editing and VST3 hosting for separately installed plugins.

The beta profile excludes native compressed-audio codec binaries pending source and redistribution review. WAV is the supported import and export format. ASIO is disabled because repeated starts retained thread handles and long-term hardware acceptance is incomplete. The ordinary development build retains those features.

Extract the complete ZIP to a writable folder and launch WaveBenchPro.exe. The runtime is included. Preserve the LICENSE and third-party notices. No plugins or samples are included.

This candidate requires the documented regression, hardware and clean-host acceptance checks before release. Native plugins run in the editor process. Multitrack projects cannot yet be saved. Large effect renders can take time and disk space. This is an unsigned draft.
