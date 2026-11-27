# Audio hardware

These backends are unreleased source changes after the frozen beta.2 draft. Existing draft downloads do not contain them.

Open **Optionen → Audio-Hardware**. Input and output each have their own backend and device selection. Apply saves the configuration locally. Shared remains the initial output mode. Select a recording endpoint explicitly before recording; a missing endpoint is not replaced by another microphone.

| Mode | Playback | Recording | Important limit |
|---|---|---|---|
| WASAPI Shared | Yes, default | Yes | Windows may convert into its shared mix format |
| WASAPI Exclusive | Optional | Not offered | Explicit format must be supported; no silent Shared fallback |
| ASIO | Optional installed driver | Supported | Optional input monitoring in a shared driver session |

For monitoring, choose the same ASIO driver in both directions and use compatible sample rates and buffer sizes. Select one output channel per input channel. Enable **ASIO-Eingang während der Aufnahme abhören** and use headphones. The switch starts off with each app launch. Only the monitor output is attenuated. Project playback and ASIO loopback are separate from this input monitoring mode.
| WASAPI Loopback | Not an output mode | Shared output capture | Captures a selected Windows render endpoint |

## Formats and buffers

**Quelle** retains the source rate. If an explicit rate is selected, the playback source must match it; the application does not silently resample to that setting. For Exclusive, choose an explicitly supported PCM encoding when the original float format is unsupported. Capability queries list stereo PCM formats and driver periods; a positive query alone does not prove a successful device start. Occupied devices and unsupported combinations cause an error. Select Shared explicitly if needed.

ASIO channel maps use numbers starting at 1 in the configuration dialog (for example, `1,2`); stored driver indices are zero-based. The source channel count must match the output mapping. An empty map uses the adapter's default mapping. An empty frame field uses the preferred driver frame count; explicit frame counts must satisfy driver constraints. Open the driver's own panel when available; changes there affect the driver and should be made deliberately. No driver is installed automatically.

The bottom hardware status shows the actual endpoint, backend, source/output format, requested buffer and API-reported value, plus read-ahead underruns/errors. Shared also shows the Windows mix format. ASIO reports its initialized frame count and native channel sample type. API buffer/latency values and the cursor compensation are not measured acoustic latency.

The prepared multitrack ring is a separate setting: 4/8/16 × 1024 frames at 48 kHz add approximately 85/171/341 ms of queued audio before the hardware buffer. Larger queues delay mixer control changes. Live VST audition also has read-ahead; knob changes are not sample-instantaneous.

## Device changes and failures

An affected endpoint removal, format change or change to an explicitly selected Windows-default output stops playback/capture. Errors are shown and explicit device reselection is required before restarting. A pinned input does not follow a different Windows-default input. Read and preserve any partial-recording recovery path shown after a capture error.

ASIO capture reserves space for the negotiated packet size and aims for about 650 ms of writer headroom. The allocation is bounded by packet count and a 16 MiB memory budget. At 48 kHz with 256 stereo frames, 122 packets fit ahead of the packet held by the file writer. A full reservoir or a reported discontinuity stops capture and preserves the completed part of the WAV.

Stop waits for resource ownership to end before replacement. If native retirement fails, the old resources remain owned and a new player is blocked; retry Stop. Without a plugin/driver isolation process, a hanging native call cannot be forcibly interrupted safely.

Short hardware checks covered Behringer USB WDM Shared/Exclusive, Realtek digital Shared/Exclusive and the genuine Behringer ASIO driver. Behringer WDM rejected 96/192 kHz Exclusive PCM16 in this setup. After 100 ASIO playback restarts, process handles remained above the warm baseline despite collection; open/close-only and dispatcher-only checks stayed flat. The outstanding buffer/start/stop resource investigation prevents complete ASIO acceptance. Support depends on the particular endpoint, driver and format; this is not a guarantee for every device. No electrical/acoustic roundtrip setup or clean SDK-free Windows host has been validated.
