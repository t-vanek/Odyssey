# Native dependencies and release packaging

Odyssey does not download executable code at runtime. Managed NuGet dependencies are restored during build; the only native library loaded explicitly by Odyssey code is the 7-Zip engine used through SharpSevenZip. Native assets pulled transitively by .NET, Avalonia, and SQLite remain handled by RID-specific `dotnet publish`.

## Release contents

The supported portable release RIDs are `win-x64` and `linux-x64`:

| RID | Native 7-Zip asset | Provenance |
|---|---|---|
| `win-x64` | `x64/7z.dll` | Supplied by the pinned SharpSevenZip NuGet package and accepted only when its SHA-256 matches the reviewed manifest. The unused x86 DLL is removed. |
| `linux-x64` | `7z.so` | Built on Ubuntu 22.04 from the exact unmodified upstream source commit in `eng/native-dependencies.json`. Package-supplied PE DLLs are removed. |

Each archive also contains `native-assets.json`, `THIRD-PARTY-NOTICES.md`, `licenses/7-Zip-LICENSE.txt`, and `licenses/SharpSevenZip-LICENSE.txt`. The native manifest records the RID, relative path, component versions, upstream commit, and actual packaged SHA-256. `eng/verify-packaged-native-assets.sh` extracts the final container, rejects unsafe declared paths, and compares the file with that digest.

The Linux shared object is dynamically linked to the standard C/C++ runtime. Building on Ubuntu 22.04 intentionally provides an older glibc baseline than `ubuntu-latest`; a distribution must still provide compatible `glibc`, `libstdc++`, `libgcc`, and `libm`. At startup Odyssey first tries an explicitly configured path, then the packaged asset, then known system locations. A candidate must be a loadable shared library with the 7-Zip `CreateObject` export. Failure disables native-only formats rather than terminating the application.

## Reproducing and testing the native build

The following commands require `bash`, `git`, a C++ toolchain, `make`, `jq`, `file`, and standard ELF tools:

```bash
./eng/verify-native-manifest.sh
./eng/build-native-7zip.sh artifacts/native/linux-x64
ODYSSEY_7ZIP_LIBRARY="$PWD/artifacts/native/linux-x64/7z.so" \
  dotnet test Odyssey.slnx --configuration Release
```

The build performs a shallow fetch of only the pinned commit, compares the checked-out commit and upstream license digest, compiles the full `Format7zF` shared library, verifies that the output is an x86-64 ELF file, and rejects unresolved dynamic dependencies. It overrides upstream warnings-as-errors for current GCC diagnostics but does not patch the source. Build-output hashes are recorded per package rather than pinned globally because compiler and linker versions can legitimately change the resulting bytes.

## Updating 7-Zip or SharpSevenZip

An update is a reviewed source change, not an automatic download:

1. Review the upstream release, license, security history, supported archive behavior, and .NET compatibility.
2. Update the managed package reference and all corresponding fields in `eng/native-dependencies.json`.
3. Recompute hashes from the exact restored package and exact upstream commit; never weaken a failed hash check.
4. Run manifest validation, the native Linux build, the full test suite with the configured library, both package-stage paths, final-container verification, and the vulnerability audit.
5. Update `THIRD-PARTY-NOTICES.md` and this document if licensing, paths, ABI, or build requirements changed.
6. Let hosted CI verify both Windows and Linux before creating a semantic-version tag.

Users may replace the library under the applicable LGPL terms or select a compatible build with `ODYSSEY_7ZIP_LIBRARY`. A replacement is still subject to load/export preflight and all archive safety policies. Exact notices and source links are in `THIRD-PARTY-NOTICES.md`; the complete license texts ship with every release.
