# Third-party native components

Odyssey release archives include replaceable native components. They are not loaded from the network at runtime.

## 7-Zip 26.02

- Copyright © 1999–2026 Igor Pavlov.
- Source: <https://github.com/ip7z/7zip/tree/f9d78aff31a5f2521ae7ddbdc97c4a8855808959>
- Project: <https://www.7-zip.org/>
- License: GNU LGPL 2.1 or later, with separately identified BSD-licensed portions and the unRAR restriction described in `licenses/7-Zip-LICENSE.txt`.
- Windows releases use the x64 `7z.dll` supplied by the pinned SharpSevenZip NuGet package. Linux releases compile the full `Format7zF` shared library from the pinned, unmodified upstream source commit.

The Linux build only overrides upstream's warnings-as-errors setting for compatibility with newer GCC diagnostics; it does not patch source files. Odyssey loads the library through a shared-library boundary. A compatible replacement can be selected with the `ODYSSEY_7ZIP_LIBRARY` environment variable.

## SharpSevenZip 2.0.109

- Copyright © the SharpSevenZip contributors.
- Source: <https://github.com/JeremyAnsel/SharpSevenZip/tree/105964f1c060481a17d20d084aacaecace92ffab>
- License: GNU LGPL 3.0 or later. The complete package license is included as `licenses/SharpSevenZip-LICENSE.txt`.

Exact versions, source commits, and integrity values are recorded in `native-assets.json` inside each release package and in `eng/native-dependencies.json` in the Odyssey source tree.
