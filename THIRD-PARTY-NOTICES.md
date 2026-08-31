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

## Tesseract OCR 5.5.0

- Copyright © the Tesseract authors.
- Source: <https://github.com/tesseract-ocr/tesseract/tree/64eab6c457b2337dd690746a5fde5c222b40d5f8>
- Binary package: <https://github.com/cyanfish/naps2-tesseract/tree/95d99470446d1b87610d20dab8b9ab277b39e5dc>
- License: Apache License 2.0. The complete package license and author list are included as `licenses/NAPS2-Tesseract-LICENSE.txt` and `licenses/Tesseract-AUTHORS.txt`.

Portable Windows x64 and Linux x64 releases include one platform-specific Tesseract command-line executable. It is invoked as a separate process and can be replaced or overridden by an explicitly configured compatible executable.

## Tesseract fast language data

- Copyright © the Tesseract authors.
- Source: <https://github.com/tesseract-ocr/tessdata_fast/tree/87416418657359cb625c412a48b6e1d6d41c29bd>
- Included models: Czech (`ces.traineddata`) and English (`eng.traineddata`).
- License: Apache License 2.0. The complete license is included as `licenses/Tesseract-traineddata-LICENSE.txt`.

## Libraries statically included in the Tesseract executable

The bundled Tesseract binaries include code from the following pinned projects. Their complete license notices ship with every release:

- Leptonica, source commit `63aef18d98432b8582a1565e241f7bd2ee9cc8d9`, license in `licenses/Leptonica-LICENSE.txt`.
- libjpeg-turbo, source commit `8ecba3647edb6dd940463fedf38ca33a8e2a73d1`, licenses in `licenses/libjpeg-turbo-LICENSE.md`.
- libpng, source commit `51f5bd68b9b806d2c92b4318164d28b49357da31`, license in `licenses/libpng-LICENSE.txt`.
- zlib, source commit `51b7f2abdade71cd9bb0e7a373ef2610ec6f9daf`, license in `licenses/zlib-LICENSE.txt`.

Exact versions, source commits, and integrity values are recorded in `native-assets.json` inside each release package and in `eng/native-dependencies.json` in the Odyssey source tree.
