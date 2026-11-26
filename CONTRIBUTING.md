# Contributing

Discuss reproducible defects/proposals in [Issues](https://github.com/radioman82/WaveBenchPro/issues) before large changes. Build on Windows with global.json's SDK, run existing tests and add focused regressions for audio/lifecycle changes.

Keep private audio, plugins, logs, SDKs and generated files out of commits. Use synthetic examples and state what was actually checked.

Keep blocking IO, plugin calls and unbounded waits out of device callbacks. Render equivalence, loop behavior, ownership and length changes are user-visible.

**Licensing is undecided.** The owner must establish contribution/reuse terms before third-party code contributions can be accepted. Do not assume an open-source license or rights to someone else's samples/plugins. There is no promised response schedule.
