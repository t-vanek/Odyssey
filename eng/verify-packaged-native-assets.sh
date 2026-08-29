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
[[ "$(jq -er '.rid' "$asset_manifest")" == "$rid" ]] || { echo "Native RID mismatch." >&2; exit 1; }

relative_path=$(jq -er '.nativeLibraries[] | select(.name == "7-Zip") | .path' "$asset_manifest")
expected_hash=$(jq -er '.nativeLibraries[] | select(.name == "7-Zip") | .sha256' "$asset_manifest")
[[ "$relative_path" =~ ^[A-Za-z0-9._/-]+$ && "$relative_path" != /* && "$relative_path" != ../* &&
   "$relative_path" != */../* && "$relative_path" != */.. ]] || {
  echo "Unsafe native asset path." >&2
  exit 1
}
native_path="$extract_dir/$relative_path"
[[ -f "$native_path" ]] || { echo "Declared native library is missing." >&2; exit 1; }
[[ "$(sha256sum "$native_path" | cut -d' ' -f1)" == "$expected_hash" ]] || {
  echo "Packaged native library hash mismatch." >&2
  exit 1
}
