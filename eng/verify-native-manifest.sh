#!/usr/bin/env bash
set -euo pipefail

repo_root=$(cd "$(dirname "${BASH_SOURCE[0]}")/.." && pwd)
manifest="$repo_root/eng/native-dependencies.json"
project="$repo_root/src/Odyssey.Infrastructure/Odyssey.Infrastructure.csproj"

jq -e '.schemaVersion == 1
  and (.sevenZip.version | type == "string")
  and (.sevenZip.sourceCommit | test("^[0-9a-f]{40}$"))
  and .sevenZip.sourceRepository == "https://github.com/ip7z/7zip"
  and (.sevenZip.licenseSha256 | test("^[0-9a-f]{64}$"))
  and (.sevenZip.windowsX64Sha256 | test("^[0-9a-f]{64}$"))
  and (.sharpSevenZip.version | type == "string")
  and .sharpSevenZip.sourceRepository == "https://github.com/JeremyAnsel/SharpSevenZip"
  and (.sharpSevenZip.sourceCommit | test("^[0-9a-f]{40}$"))
  and (.sharpSevenZip.licenseSha256 | test("^[0-9a-f]{64}$"))' "$manifest" >/dev/null

manifest_version=$(jq -er '.sharpSevenZip.version' "$manifest")
project_version=$(sed -n 's/.*PackageReference Include="SharpSevenZip" Version="\([^"]*\)".*/\1/p' "$project")
if [[ "$manifest_version" != "$project_version" ]]; then
  echo "SharpSevenZip package and native manifest versions differ." >&2
  exit 1
fi
