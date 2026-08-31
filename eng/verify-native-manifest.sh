#!/usr/bin/env bash
set -euo pipefail

repo_root=$(cd "$(dirname "${BASH_SOURCE[0]}")/.." && pwd)
manifest="$repo_root/eng/native-dependencies.json"
project="$repo_root/src/Odyssey.Infrastructure/Odyssey.Infrastructure.csproj"
desktop_project="$repo_root/src/Odyssey.Desktop/Odyssey.Desktop.csproj"

jq -e '.schemaVersion == 1
  and (.sevenZip.version | type == "string")
  and (.sevenZip.sourceCommit | test("^[0-9a-f]{40}$"))
  and .sevenZip.sourceRepository == "https://github.com/ip7z/7zip"
  and (.sevenZip.licenseSha256 | test("^[0-9a-f]{64}$"))
  and (.sevenZip.windowsX64Sha256 | test("^[0-9a-f]{64}$"))
  and (.sharpSevenZip.version | type == "string")
  and .sharpSevenZip.sourceRepository == "https://github.com/JeremyAnsel/SharpSevenZip"
  and (.sharpSevenZip.sourceCommit | test("^[0-9a-f]{40}$"))
  and (.sharpSevenZip.licenseSha256 | test("^[0-9a-f]{64}$"))
  and .tesseract.binaryPackage == "NAPS2.Tesseract.Binaries"
  and (.tesseract.binaryPackageVersion | type == "string")
  and .tesseract.binaryPackageRepository == "https://github.com/cyanfish/naps2-tesseract"
  and (.tesseract.binaryPackageCommit | test("^[0-9a-f]{40}$"))
  and .tesseract.sourceRepository == "https://github.com/tesseract-ocr/tesseract"
  and (.tesseract.sourceCommit | test("^[0-9a-f]{40}$"))
  and (.tesseract.licenseSha256 | test("^[0-9a-f]{64}$"))
  and (.tesseract.authorsSha256 | test("^[0-9a-f]{64}$"))
  and (.tesseract.linuxX64Sha256 | test("^[0-9a-f]{64}$"))
  and (.tesseract.windowsX64Sha256 | test("^[0-9a-f]{64}$"))
  and (.tesseract.leptonicaCommit | test("^[0-9a-f]{40}$"))
  and (.tesseract.leptonicaLicenseSha256 | test("^[0-9a-f]{64}$"))
  and (.tesseract.libjpegTurboCommit | test("^[0-9a-f]{40}$"))
  and (.tesseract.libjpegTurboLicenseSha256 | test("^[0-9a-f]{64}$"))
  and (.tesseract.libpngCommit | test("^[0-9a-f]{40}$"))
  and (.tesseract.libpngLicenseSha256 | test("^[0-9a-f]{64}$"))
  and (.tesseract.zlibCommit | test("^[0-9a-f]{40}$"))
  and (.tesseract.zlibLicenseSha256 | test("^[0-9a-f]{64}$"))
  and .tessdataFast.sourceRepository == "https://github.com/tesseract-ocr/tessdata_fast"
  and (.tessdataFast.sourceCommit | test("^[0-9a-f]{40}$"))
  and (.tessdataFast.licenseSha256 | test("^[0-9a-f]{64}$"))
  and (.tessdataFast.languages.ces | test("^[0-9a-f]{64}$"))
  and (.tessdataFast.languages.eng | test("^[0-9a-f]{64}$"))' "$manifest" >/dev/null

manifest_version=$(jq -er '.sharpSevenZip.version' "$manifest")
project_version=$(sed -n 's/.*PackageReference Include="SharpSevenZip" Version="\([^"]*\)".*/\1/p' "$project")
if [[ "$manifest_version" != "$project_version" ]]; then
  echo "SharpSevenZip package and native manifest versions differ." >&2
  exit 1
fi

tesseract_manifest_version=$(jq -er '.tesseract.binaryPackageVersion' "$manifest")
tesseract_project_version=$(sed -n 's/.*PackageReference Include="NAPS2.Tesseract.Binaries" Version="\([^"]*\)".*/\1/p' "$desktop_project")
if [[ "$tesseract_manifest_version" != "$tesseract_project_version" ]]; then
  echo "NAPS2 Tesseract package and native manifest versions differ." >&2
  exit 1
fi

verify_hash() {
  local path=$1
  local expected=$2
  [[ -f "$path" ]] || { echo "Required Tesseract asset is missing: $path" >&2; exit 1; }
  [[ "$(sha256sum "$path" | cut -d' ' -f1)" == "$expected" ]] || {
    echo "Tesseract asset hash does not match the reviewed manifest: $path" >&2
    exit 1
  }
}

verify_hash "$repo_root/src/Odyssey.Desktop/Ocr/tessdata/ces.traineddata" \
  "$(jq -er '.tessdataFast.languages.ces' "$manifest")"
verify_hash "$repo_root/src/Odyssey.Desktop/Ocr/tessdata/eng.traineddata" \
  "$(jq -er '.tessdataFast.languages.eng' "$manifest")"
verify_hash "$repo_root/src/Odyssey.Desktop/Ocr/tessdata/LICENSE" \
  "$(jq -er '.tessdataFast.licenseSha256' "$manifest")"
verify_hash "$repo_root/src/Odyssey.Desktop/Ocr/licenses/Leptonica-LICENSE.txt" \
  "$(jq -er '.tesseract.leptonicaLicenseSha256' "$manifest")"
verify_hash "$repo_root/src/Odyssey.Desktop/Ocr/licenses/libjpeg-turbo-LICENSE.md" \
  "$(jq -er '.tesseract.libjpegTurboLicenseSha256' "$manifest")"
verify_hash "$repo_root/src/Odyssey.Desktop/Ocr/licenses/libpng-LICENSE.txt" \
  "$(jq -er '.tesseract.libpngLicenseSha256' "$manifest")"
verify_hash "$repo_root/src/Odyssey.Desktop/Ocr/licenses/zlib-LICENSE.txt" \
  "$(jq -er '.tesseract.zlibLicenseSha256' "$manifest")"
