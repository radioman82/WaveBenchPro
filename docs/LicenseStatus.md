# License and redistribution status

## Application code

No project license has been chosen by the owner. No LICENSE file granting reuse rights has been added. Public repository visibility does not itself grant a general open-source license; see [GitHub's licensing guidance](https://docs.github.com/en/repositories/managing-your-repositorys-settings-and-features/customizing-your-repository/licensing-a-repository).

The beta package is a **draft**, not approved for public redistribution. The owner must establish application/contribution terms before publishing it.

## Managed dependencies

Application restore resolves the NAudio family at 3.3.0 (MIT in NuGet metadata), NAudio.Lame 2.1.0 (MIT wrapper) and org.sndfile.runtime.win-x64 1.2.2. See packages.lock.json for exact dependency resolution.

The NAudio package's recorded source commit was not retrievable from its public upstream during preparation. The included upstream NAudio MIT notice is from the accessible release/2.x branch; exact correspondence to the 3.3.0 binaries remains to be confirmed. The .NET runtime's supplied license/notices are included where available.

## Native codecs

The runtime package contains sndfile, FLAC, Ogg, Vorbis, Opus, mpg123/out123/syn123 and libmp3lame DLLs. NAudio.Lame additionally supplies native 32/64-bit LAME DLLs. [NativeDependencies.json](NativeDependencies.json) records the exact packaged binary hashes.

The runtime package metadata says Apache-2.0, while the upstream [libsndfile licensing page](https://libsndfile.github.io/libsndfile/#licensing) identifies LGPL terms. Package metadata does not replace each bundled library's license.

- NAudio / NAudio.Lame managed wrappers: MIT metadata/notices.
- libsndfile, LAME and mpg123 family: LGPL-family notices require review against the exact native builds and applicable terms.
- FLAC, Ogg, Vorbis and Opus: upstream permissive notices are collected.
- .NET runtime and Windows desktop runtime: their own bundled notices apply.

[LAME's official licensing notes](https://lame.sourceforge.io/license.txt) also distinguish its library use and historical decoder code. The precise source/build provenance and codec versions of every supplied DLL have not been established merely from a NuGet version.

## Publication gates

Before making the beta public:

1. Owner chooses the application license and distribution/contribution terms.
2. Confirm native DLL provenance, corresponding source/rebuild information and library replacement requirements as applicable; retain all required notices.
3. Resolve the difference between runtime package metadata and native upstream terms.
4. Confirm the NAudio 3.3.0 source/notices correspondence.
5. Complete hardware acceptance and test the complete archive on a clean supported Windows machine.

The files in licenses/ are third-party texts, not a license grant for WaveBench Pro. They are a preparation inventory, not a declaration that every redistribution condition has been fulfilled. No third-party VST plugin or commercial sample is bundled.
