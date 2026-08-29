#!/usr/bin/env bash
set -euo pipefail

if [[ $# -ne 1 ]]; then
  echo "Usage: $0 OUTPUT_DIRECTORY" >&2
  exit 2
fi

repo_root=$(cd "$(dirname "${BASH_SOURCE[0]}")/.." && pwd)
manifest="$repo_root/eng/native-dependencies.json"
output_dir=$(mkdir -p "$1" && cd "$1" && pwd)
source_commit=$(jq -er '.sevenZip.sourceCommit' "$manifest")
source_repository=$(jq -er '.sevenZip.sourceRepository' "$manifest")
expected_license=$(jq -er '.sevenZip.licenseSha256' "$manifest")
build_root=$(mktemp -d)
trap 'rm -rf "$build_root"' EXIT

git -C "$build_root" init --quiet source
git -C "$build_root/source" remote add origin "$source_repository.git"
git -C "$build_root/source" fetch --quiet --depth 1 origin "$source_commit"
git -C "$build_root/source" checkout --quiet --detach FETCH_HEAD

actual_commit=$(git -C "$build_root/source" rev-parse HEAD)
if [[ "$actual_commit" != "$source_commit" ]]; then
  echo "7-Zip checkout does not match the pinned source commit." >&2
  exit 1
fi

license="$build_root/source/DOC/License.txt"
actual_license=$(sha256sum "$license" | cut -d' ' -f1)
if [[ "$actual_license" != "$expected_license" ]]; then
  echo "7-Zip license hash does not match the reviewed manifest." >&2
  exit 1
fi

bundle="$build_root/source/CPP/7zip/Bundles/Format7zF"
build_jobs=$(getconf _NPROCESSORS_ONLN)
if (( build_jobs > 4 )); then build_jobs=4; fi
make --silent --no-print-directory -C "$bundle" -f ../../cmpl_gcc.mak -j"$build_jobs" \
  CFLAGS_WARN_WALL='-Wall -Wextra'

install -m 0755 "$bundle/b/g/7z.so" "$output_dir/7z.so"
cp "$license" "$output_dir/7-Zip-LICENSE.txt"
file "$output_dir/7z.so" | grep -q 'ELF 64-bit.*x86-64'
if ldd "$output_dir/7z.so" | grep -q 'not found'; then
  echo "The compiled 7z.so has unresolved runtime dependencies." >&2
  exit 1
fi
(cd "$output_dir" && sha256sum 7z.so > 7z.so.sha256)
