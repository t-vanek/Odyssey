# Native dependencies and release packaging

Odyssey does not download executable code or OCR models at runtime. Managed NuGet dependencies are restored during build. Odyssey explicitly uses the 7-Zip engine through SharpSevenZip and invokes a bundled Tesseract executable for local OCR. Native assets pulled transitively by .NET, Avalonia, and SQLite remain handled by RID-specific `dotnet publish`.

## Release contents

The supported portable release RIDs are `win-x64` and `linux-x64`:

| RID | Native 7-Zip asset | Tesseract executable |
|---|---|---|
| `win-x64` | `x64/7z.dll`, supplied by the pinned SharpSevenZip package | `ocr/tesseract.exe` from the pinned NAPS2 binary package |
| `linux-x64` | `7z.so`, built from the pinned upstream source | `ocr/tesseract` from the pinned NAPS2 binary package |

Each archive also contains the pinned `ocr/tessdata/ces.traineddata` and `ocr/tessdata/eng.traineddata` models, `native-assets.json`, `THIRD-PARTY-NOTICES.md`, and complete license notices under `licenses/`. The native manifest records the RID, relative paths, component versions, upstream commits, and actual packaged SHA-256 values. `eng/verify-packaged-native-assets.sh` extracts the final container, rejects unsafe declared paths, checks every declared executable and model against its digest, and asks the packaged Tesseract to enumerate both languages.

The Linux shared object is dynamically linked to the standard C/C++ runtime. Building on Ubuntu 22.04 intentionally provides an older glibc baseline than `ubuntu-latest`; a distribution must still provide compatible `glibc`, `libstdc++`, `libgcc`, and `libm`. At startup Odyssey first tries an explicitly configured path, then the packaged asset, then known system locations. A candidate must be a loadable shared library with the 7-Zip `CreateObject` export. Failure disables native-only formats rather than terminating the application.

Tesseract resolution follows a separate order: an explicitly configured executable, the executable under the application's `ocr/` directory, `PATH`, and finally known Windows installation locations. The bundled executable automatically uses the adjacent `ocr/tessdata/` directory. Odyssey accepts OCR only after a `--list-langs` probe confirms both `ces` and `eng`; missing or damaged OCR assets disable image indexing without preventing the application from starting.

## Reproducing and testing the native build

The following commands require `bash`, `git`, a C++ toolchain, `make`, `jq`, `file`, and standard ELF tools:

```bash
./eng/verify-native-manifest.sh
./eng/build-native-7zip.sh artifacts/native/linux-x64
ODYSSEY_7ZIP_LIBRARY="$PWD/artifacts/native/linux-x64/7z.so" \
  dotnet test Odyssey.slnx --configuration Release
```

The build performs a shallow fetch of only the pinned commit, compares the checked-out commit and upstream license digest, compiles the full `Format7zF` shared library, verifies that the output is an x86-64 ELF file, and rejects unresolved dynamic dependencies. It overrides upstream warnings-as-errors for current GCC diagnostics but does not patch the source. Build-output hashes are recorded per package rather than pinned globally because compiler and linker versions can legitimately change the resulting bytes.

## Updating native or OCR dependencies

An update is a reviewed source change, not an automatic download:

1. Review the upstream release, license, security history, supported behavior, and .NET compatibility.
2. Update the managed package reference and all corresponding fields in `eng/native-dependencies.json`.
3. Recompute hashes from the exact restored package, language data, license files, and upstream commits; never weaken a failed hash check.
4. Run manifest validation, the native Linux build, the full test suite with the configured library, both package-stage paths, final-container verification, and the vulnerability audit.
5. Update `THIRD-PARTY-NOTICES.md` and this document if licensing, paths, ABI, or build requirements changed.
6. Let hosted CI verify both Windows and Linux before creating an accepted stable or preview release tag.

Users may replace the library under the applicable LGPL terms or select a compatible build with `ODYSSEY_7ZIP_LIBRARY`. A replacement is still subject to load/export preflight and all archive safety policies. Exact notices and source links are in `THIRD-PARTY-NOTICES.md`; the complete license texts ship with every release.
