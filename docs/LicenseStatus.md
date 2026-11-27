# License and redistribution status

## Application code

The owner selected the [MIT License](../LICENSE) for the project's own code. The MIT License permits commercial use and redistribution, including sale, subject to its notice requirements. Third-party components remain subject to their own licenses.

The beta package remains a **draft**, pending the third-party and acceptance checks below. Contributor rights and any previous grants should be verified for the relevant contributions.

## Managed dependencies

Application restore resolves the NAudio family at 3.3.0 (MIT in NuGet metadata), NAudio.Lame 2.1.0 (MIT wrapper) and org.sndfile.runtime.win-x64 1.2.2. See packages.lock.json for exact dependency resolution.

The NAudio 3.3.0 family, including the explicitly added NAudio.Asio 3.3.0 package, records source commit `4f8889e2bd34c3978a25652c6c172fa1502ccba3`. The exact [upstream LICENSE](https://raw.githubusercontent.com/naudio/NAudio/4f8889e2bd34c3978a25652c6c172fa1502ccba3/LICENSE) is now included as licenses/NAudio-MIT.txt (SHA-256 `e518e168af44d37da3de236e719c986169e47b46b90e37c1355056175744e037`). This resolves the earlier source-notice lookup gap, not the separate native-codec gates. The .NET runtime's supplied license/notices are included where available.

## Native codecs

The runtime package contains sndfile, FLAC, Ogg, Vorbis, Opus, mpg123/out123/syn123 and libmp3lame DLLs. NAudio.Lame supplies native LAME binaries for both Windows architectures. The current win-x64 publishing target emits only the x64 codec DLLs beside the executable. The [native dependency inventory](NativeDependencies.json) records hashes from the restored packages.

The runtime package metadata says Apache-2.0, while the upstream [libsndfile licensing page](https://libsndfile.github.io/libsndfile/#licensing) identifies LGPL terms. Package metadata does not replace each bundled library's license.

- NAudio / NAudio.Lame managed wrappers: MIT metadata/notices.
- libsndfile, LAME and mpg123 family: LGPL-family notices require review against the exact native builds and applicable terms.
- FLAC, Ogg, Vorbis and Opus: upstream permissive notices are collected.
- .NET runtime and Windows desktop runtime: their own bundled notices apply.

[LAME's official licensing notes](https://lame.sourceforge.io/license.txt) also distinguish its library use and historical decoder code. The precise source/build provenance and codec versions of every supplied DLL have not been established merely from a NuGet version. [NativeDependencyProvenance.json](NativeDependencyProvenance.json) adds exported runtime version checks: libsndfile 1.2.2, FLAC 1.4.3, Opus 1.4, Vorbis 1.3.7 and LAME 3.100. Other exact native versions remain unconfirmed. The runtime package records libsndfile commit `12d89d85e1a4dacb17e6ab7e4561920b318b7787`; its direct upstream COPYING lookup returned 404, and exact build recipes/corresponding source have not been recovered.

For libraries distributed under LGPL 2.1, review the applicable route in [section 6](https://spdx.org/licenses/LGPL-2.1-only.html), including notices, permitted modification/reverse engineering for debugging modifications, corresponding source and replacement or relinking as applicable. A local FLAC import with the previous package loaded the extracted sndfile.dll despite a replacement beside the executable. The current publishing target excludes codec DLLs from the single-file bundle and verifies their sidecar hashes against the restored packages. Exact source/build material and the applicable distribution route still require review.

## Publication gates

Before making the beta public:

1. Verify contributor rights and prior grants for included contributions.
2. Confirm native DLL provenance, corresponding source/rebuild information and library replacement requirements as applicable; retain all required notices.
3. Resolve the difference between runtime package metadata and native upstream terms.
4. Retain the now-confirmed NAudio 3.3.0 MIT notice and exact package/source manifest.
5. Complete hardware acceptance and test the complete archive on a clean supported Windows machine.

The files in licenses/ are third-party texts, not a license grant for WaveBench Pro. They are a preparation inventory, not a declaration that every redistribution condition has been fulfilled. No third-party VST plugin or commercial sample is bundled.
