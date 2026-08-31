#!/usr/bin/env bash
set -euo pipefail

if [[ $# -ne 2 ]]; then
  echo "Usage: $0 RID ARCHIVE" >&2
  exit 2
fi

rid=$1
archive=$(cd "$(dirname "$2")" && pwd)/$(basename "$2")
extract_dir=$(mktemp -d)
trap 'rm -rf "$extract_dir"' EXIT

case "$rid" in
  win-x64) unzip -q "$archive" -d "$extract_dir" ;;
  linux-x64) tar -xzf "$archive" -C "$extract_dir" ;;
  *) echo "Unsupported package RID: $rid" >&2; exit 1 ;;
esac

asset_manifest="$extract_dir/native-assets.json"
[[ -s "$asset_manifest" ]] || { echo "native-assets.json is missing." >&2; exit 1; }
[[ -s "$extract_dir/THIRD-PARTY-NOTICES.md" ]] || { echo "Third-party notices are missing." >&2; exit 1; }
[[ -s "$extract_dir/licenses/7-Zip-LICENSE.txt" ]] || { echo "7-Zip license is missing." >&2; exit 1; }
[[ -s "$extract_dir/licenses/SharpSevenZip-LICENSE.txt" ]] || { echo "SharpSevenZip license is missing." >&2; exit 1; }
for license in \
  NAPS2-Tesseract-LICENSE.txt \
  Tesseract-AUTHORS.txt \
  Tesseract-traineddata-LICENSE.txt \
  Leptonica-LICENSE.txt \
  libjpeg-turbo-LICENSE.md \
  libpng-LICENSE.txt \
  zlib-LICENSE.txt; do
  [[ -s "$extract_dir/licenses/$license" ]] || { echo "Tesseract dependency notice is missing: $license" >&2; exit 1; }
done
[[ "$(jq -er '.rid' "$asset_manifest")" == "$rid" ]] || { echo "Native RID mismatch." >&2; exit 1; }

verify_declared_asset() {
  local relative_path=$1
  local expected_hash=$2
  [[ "$relative_path" =~ ^[A-Za-z0-9._/-]+$ && "$relative_path" != /* && "$relative_path" != ../* &&
     "$relative_path" != */../* && "$relative_path" != */.. ]] || {
    echo "Unsafe packaged asset path." >&2
    exit 1
  }
  local packaged_path="$extract_dir/$relative_path"
  [[ -f "$packaged_path" ]] || { echo "Declared packaged asset is missing: $relative_path" >&2; exit 1; }
  [[ "$(sha256sum "$packaged_path" | cut -d' ' -f1)" == "$expected_hash" ]] || {
    echo "Packaged asset hash mismatch: $relative_path" >&2
    exit 1
  }
}

while IFS=$'\t' read -r relative_path expected_hash; do
  verify_declared_asset "$relative_path" "$expected_hash"
done < <(jq -er '.nativeLibraries[] | [.path, .sha256] | @tsv' "$asset_manifest")

while IFS=$'\t' read -r relative_path expected_hash; do
  verify_declared_asset "$relative_path" "$expected_hash"
done < <(jq -er '.dataFiles[] | [.path, .sha256] | @tsv' "$asset_manifest")

[[ "$(jq -er '[.nativeLibraries[].name] | sort | join("|")' "$asset_manifest")" == "7-Zip|Tesseract OCR" ]] || {
  echo "Native asset manifest does not declare both expected libraries." >&2
  exit 1
}
[[ "$(jq -er '.dataFiles | length' "$asset_manifest")" == "2" ]] || {
  echo "Native asset manifest does not declare both OCR language files." >&2
  exit 1
}

case "$rid" in
  win-x64)
    tesseract_path="$extract_dir/ocr/tesseract.exe"
    [[ "$(od -An -tx1 -N2 "$tesseract_path" | tr -d ' \n')" == "4d5a" ]] || {
      echo "Packaged Windows Tesseract is not a PE file." >&2
      exit 1
    }
    ;;
  linux-x64)
    tesseract_path="$extract_dir/ocr/tesseract"
    [[ -x "$tesseract_path" ]] || { echo "Packaged Tesseract is not executable." >&2; exit 1; }
    "$tesseract_path" --tessdata-dir "$extract_dir/ocr/tessdata" --list-langs > "$extract_dir/tesseract-languages.txt"
    grep -qx ces "$extract_dir/tesseract-languages.txt" || { echo "Packaged Czech OCR data is unavailable." >&2; exit 1; }
    grep -qx eng "$extract_dir/tesseract-languages.txt" || { echo "Packaged English OCR data is unavailable." >&2; exit 1; }
    ;;
esac
