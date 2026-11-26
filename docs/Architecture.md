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

A fixed position history approximates the audible cursor from delivered frames and requested latency. Selection bounds are independent of seeks.

## Ownership and errors

Private cache files use ownership markers, active leases and a process-shared 16-GiB reservation budget. Orphan cleanup only removes recognized files under a verified inactive lease. Other processes can still change free disk space.

Stop requests cancellation with bounded waiting. Success follows actual worker/lease completion; timeout differs from cleanup error. Synchronous native/plugin/driver/filesystem calls cannot be forcibly interrupted.

Recording uses a dedicated writer and bounded queue. Overflow stops capture and preserves a partial WAV instead of silently dropping material.

## Build and packaging

Version: v0.3.0-beta.2. global.json pins the SDK; packages.lock.json pins application dependency resolution.

```powershell
powershell -ExecutionPolicy Bypass -File .\scripts\Publish-Portable.ps1
```

The script performs locked restore/self-contained win-x64 Release publish into a fresh ignored directory, copies public instructions/notices, creates a ZIP and computes SHA256SUMS.txt. It rejects existing directories rather than deleting them. Keep the ZIP intact, including native companion DLLs.

Deterministic compiler paths are enabled. This is a repeatable procedure, not a claim of byte-identical output across toolchains/ZIP timestamps.

Tests include simulated failures. Benchmarks are separate; playback/device-check modes really use audio hardware. Short success does not establish long-session reliability.

No ASIO/Exclusive migration, RF64, project persistence or native plugin isolation is included.
