#!/usr/bin/env bash
# OctantPath Desktop を .NET ごと 1 つの実行ファイルにする（利用者の PC に .NET は要らない）
# 使い方: scripts/publish-desktop.sh <RID>   例: linux-x64、win-x64。成果物は dist/<RID>/
set -euo pipefail
root="$(cd "$(dirname "$0")/.." && pwd)"
rid="${1:?RID (linux-x64, win-x64) を渡す}"
dotnet publish "$root/src/OctantPath.Desktop" -c Release -r "$rid" --self-contained \
  -p:PublishSingleFile=true -p:IncludeNativeLibrariesForSelfExtract=true -p:EnableCompressionInSingleFile=true \
  -p:DebugType=none -o "$root/dist/$rid"
# 同梱したものの表記: 手で書いた分（src/OctantPath.Desktop/THIRD-PARTY-NOTICES.txt）に、ビルドに使った
# .NET と ASP.NET Core のランタイムパックの LICENSE と THIRD-PARTY-NOTICES を足して、実行ファイルの横に置く
assets="$root/src/OctantPath.Desktop/obj/project.assets.json"
packages="${NUGET_PACKAGES:-$HOME/.nuget/packages}"
notices="$root/dist/$rid/THIRD-PARTY-NOTICES.txt"
cp "$root/src/OctantPath.Desktop/THIRD-PARTY-NOTICES.txt" "$notices"
for pack in Microsoft.NETCore.App.Runtime Microsoft.AspNetCore.App.Runtime; do
  name="$pack.$rid"
  version="$(python3 -c 'import json, sys
for d in json.load(open(sys.argv[1]))["project"]["frameworks"]["net8.0"].get("downloadDependencies", []):
    if d["name"].lower() == sys.argv[2].lower():
        print(d["version"].strip("[]").split(",")[0].strip())' "$assets" "$name")"
  dir="$packages/${name,,}/$version"
  [ -n "$version" ] && [ -d "$dir" ] || { echo "$name のランタイムパックが見つからない（$dir）" >&2; exit 1; }
  for file in "$dir"/*; do
    case "$(basename "${file^^}")" in
      LICENSE.TXT|THIRD-PARTY-NOTICES.TXT)
        printf '\n%s\n%s %s: %s\n%s\n' "$(printf '=%.0s' {1..78})" "$pack" "$version" "$(basename "$file")" \
          "$(printf '=%.0s' {1..78})" >> "$notices"
        cat "$file" >> "$notices" ;;
    esac
  done
  grep -q "^$pack $version: THIRD-PARTY-NOTICES" "$notices" || { echo "$name に THIRD-PARTY-NOTICES がない" >&2; exit 1; }
done
ls -l "$root/dist/$rid"
