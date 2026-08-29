#!/usr/bin/env bash
set -euo pipefail

if [[ $# -ne 3 ]]; then
  echo "Usage: $0 RID PUBLISH_DIRECTORY LINUX_NATIVE_DIRECTORY" >&2
  exit 2
fi

rid=$1
publish_dir=$(cd "$2" && pwd)
linux_native_dir=$3
repo_root=$(cd "$(dirname "${BASH_SOURCE[0]}")/.." && pwd)
manifest="$repo_root/eng/native-dependencies.json"
sharp_version=$(jq -er '.sharpSevenZip.version' "$manifest")
sharp_license_hash=$(jq -er '.sharpSevenZip.licenseSha256' "$manifest")
sharp_commit=$(jq -er '.sharpSevenZip.sourceCommit' "$manifest")
seven_zip_version=$(jq -er '.sevenZip.version' "$manifest")
seven_zip_commit=$(jq -er '.sevenZip.sourceCommit' "$manifest")
licenses="$publish_dir/licenses"
mkdir -p "$licenses"

sharp_license="$HOME/.nuget/packages/sharpsevenzip/$sharp_version/LICENSE"
if [[ ! -f "$sharp_license" ]]; then
  echo "SharpSevenZip license was not found in the restored package." >&2
  exit 1
fi
if [[ "$(sha256sum "$sharp_license" | cut -d' ' -f1)" != "$sharp_license_hash" ]]; then
  echo "SharpSevenZip license hash does not match the reviewed manifest." >&2
  exit 1
fi
cp "$sharp_license" "$licenses/SharpSevenZip-LICENSE.txt"
cp "$repo_root/THIRD-PARTY-NOTICES.md" "$publish_dir/THIRD-PARTY-NOTICES.md"

case "$rid" in
  win-x64)
    package_native="$HOME/.nuget/packages/sharpsevenzip/$sharp_version/build/x64/7z.dll"
    native_path="$publish_dir/x64/7z.dll"
    expected_hash=$(jq -er '.sevenZip.windowsX64Sha256' "$manifest")
    [[ -f "$package_native" ]] || { echo "SharpSevenZip package x64/7z.dll is missing." >&2; exit 1; }
    [[ "$(sha256sum "$package_native" | cut -d' ' -f1)" == "$expected_hash" ]] || {
      echo "SharpSevenZip x64/7z.dll hash does not match the reviewed manifest." >&2
      exit 1
    }
    mkdir -p "$(dirname "$native_path")"
    cp "$package_native" "$native_path"
    [[ "$(od -An -tx1 -N2 "$native_path" | tr -d ' \n')" == "4d5a" ]] || {
      echo "The Windows 7-Zip library is not a PE file." >&2
      exit 1
    }
    rm -rf "$publish_dir/x86"
    seven_zip_license="$linux_native_dir/7-Zip-LICENSE.txt"
    ;;
  linux-x64)
    source_native="$linux_native_dir/7z.so"
    seven_zip_license="$linux_native_dir/7-Zip-LICENSE.txt"
    [[ -f "$source_native" ]] || { echo "Built Linux 7z.so is missing." >&2; exit 1; }
    [[ -f "$linux_native_dir/7z.so.sha256" ]] || { echo "Linux 7-Zip checksum is missing." >&2; exit 1; }
    (cd "$linux_native_dir" && sha256sum --check --status 7z.so.sha256) || {
      echo "Linux 7-Zip artifact changed after the verified build." >&2
      exit 1
    }
    [[ "$(od -An -tx1 -N4 "$source_native" | tr -d ' \n')" == "7f454c46" ]] || {
      echo "The Linux 7-Zip library is not an ELF file." >&2
      exit 1
    }
    install -m 0755 "$source_native" "$publish_dir/7z.so"
    rm -rf "$publish_dir/x86" "$publish_dir/x64"
    native_path="$publish_dir/7z.so"
    ;;
  *)
    echo "Unsupported native asset RID: $rid" >&2
    exit 1
    ;;
esac

[[ -f "$seven_zip_license" ]] || { echo "7-Zip license is missing." >&2; exit 1; }
expected_7zip_license=$(jq -er '.sevenZip.licenseSha256' "$manifest")
[[ "$(sha256sum "$seven_zip_license" | cut -d' ' -f1)" == "$expected_7zip_license" ]] || {
  echo "7-Zip license hash does not match the reviewed manifest." >&2
  exit 1
}
cp "$seven_zip_license" "$licenses/7-Zip-LICENSE.txt"

native_hash=$(sha256sum "$native_path" | cut -d' ' -f1)
jq -n \
  --arg rid "$rid" \
  --arg path "${native_path#"$publish_dir/"}" \
  --arg sha256 "$native_hash" \
  --arg sevenZipVersion "$seven_zip_version" \
  --arg sevenZipCommit "$seven_zip_commit" \
  --arg sharpSevenZipVersion "$sharp_version" \
  --arg sharpSevenZipCommit "$sharp_commit" \
  '{
    schemaVersion: 1,
    rid: $rid,
    nativeLibraries: [{
      name: "7-Zip",
      version: $sevenZipVersion,
      path: $path,
      sha256: $sha256,
      sourceCommit: $sevenZipCommit
    }],
    managedWrappers: [{
      name: "SharpSevenZip",
      version: $sharpSevenZipVersion,
      sourceCommit: $sharpSevenZipCommit
    }]
  }' > "$publish_dir/native-assets.json"
