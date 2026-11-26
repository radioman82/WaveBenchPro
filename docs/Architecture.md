# Architecture

## Application and documents

WaveBench Pro is a Windows x64 WPF application targeting .NET 9 with NAudio 3.3.0, VST3/SoundFile hosting, NAudio.Lame and a Windows libsndfile runtime.

AudioDocument owns state and undo. WAVs can reference source segments with independent IAudioSource readers; compressed imports decode into memory. Waveform peaks/spectra are separate background work. History has no fixed count limit, but disk/memory capacity still applies.

## Rendering

MultitrackProject holds tracks/clips. BlockMixdownRenderer sums sequential blocks using an active-clip interval index. Resampling and WSOLA retain sequential state. VST tracks are prepared as private, length-limited float WAVs with insert order preserved.

Float mixing retains headroom; quantization/encoding happens at export. Peak clipping is a separate effect.

## Experimental transport

PreparedMultitrackSession renders occupied tracks using the block kernel. No cross-session reuse is performed; changes create a new snapshot/preparation.

PreparedMultitrackPlayback serializes generations and owns even unpublished queues until workers/cancellation complete. Session completion includes active readers and actual lease cleanup.

BufferedBlockWaveProvider is a bounded single-producer/single-consumer ring. A dedicated producer performs disk IO and mixer controls. The callback copies blocks, records positions and signals space; it does not call plugins, decode or wait for the producer. Underrun silence pauses the source timeline.

A fixed position history approximates the audible cursor from delivered frames and backend-reported latency. These values do not measure acoustic roundtrip latency. Selection bounds are independent of seeks.

## Ownership and errors

Private cache files use ownership markers, active leases and a process-shared 16-GiB reservation budget. Orphan cleanup only removes recognized files under a verified inactive lease. Other processes can still change free disk space.

Stop requests cancellation with bounded waiting. Success follows actual worker/lease completion; timeout differs from cleanup error. Synchronous native/plugin/driver/filesystem calls cannot be forcibly interrupted.

Recording uses a dedicated writer and preallocated bounded packet queue. Native span callbacks only copy data; overflow/discontinuity stops capture and preserves a partial WAV instead of silently dropping material.

## Hardware backends

AudioHardwareBackend centralizes endpoint enumeration, capability queries and playback/capture adapters. NAudio 3.3.0 WasapiPlayerBuilder / WasapiRecorderBuilder replace obsolete WASAPI constructors; the separately pinned NAudio.Asio package supplies AsioDevice. Shared remains default. Optional Exclusive playback validates the explicit source/output format without an implicit backend fallback. ASIO control calls run on a dedicated STA dispatcher; playback and recording were validated separately, not full-duplex.

Both previous and prepared multitrack engines feed the same hardware abstraction. File-backed and live-plugin providers use HardwareReadAhead: the producer runs file IO/plugin processing and the native callback copies bounded blocks. Memory-backed previous-engine audio and already buffered prepared playback avoid an additional queue. No cross-session cache reuse or mixer architecture change was added.

AudioPlaybackLifetime retains sources when native retirement fails and prevents a concurrent replacement. Native close must finish before releasing read-ahead workers/endpoints. BufferedBlockWaveProvider shutdown includes its wait-handle disposal after active reads finish. Native calls can still block indefinitely without isolation.

Stable endpoint IDs/driver names and modes persist independently for input/output. Native device/default/format notifications stop the affected stream and require explicit reselection. Default output binding does not imply automatic default-input recording. See [Audio hardware](AudioHardware.md).

## Build and packaging

Version: v0.3.0-beta.2. global.json pins the SDK; packages.lock.json pins application dependency resolution.

```powershell
powershell -ExecutionPolicy Bypass -File .\scripts\Publish-Portable.ps1
```

The script performs locked restore/self-contained win-x64 Release publish into a fresh ignored directory, copies public instructions/notices, creates a ZIP and computes SHA256SUMS.txt. It rejects existing directories rather than deleting them. Keep the ZIP intact, including native companion DLLs.

Deterministic compiler paths are enabled. This is a repeatable procedure, not a claim of byte-identical output across toolchains/ZIP timestamps.

Tests include simulated failures. Benchmarks are separate; playback/device-check modes really use audio hardware. Short success does not establish long-session reliability.

RF64, project persistence and native plugin isolation are not included. The current hardware migration is unreleased; beta.2 draft binaries and version fields remain unchanged pending owner approval.
