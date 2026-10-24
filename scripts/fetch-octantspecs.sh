#!/usr/bin/env bash
# テストが読む OctantSpecs（契約3（OctantPath to OctantCompass）の例）を、OCTANTSPECS_REF の版で .octantspecs/ に取ってくる
# OCTANTSPECS_REF は OctantSpecs の main（2026-10-23。履歴を消した公開の写しでもそのまま使えるよう、コミットでは固定しない）
# 取る先は OCTANTSPECS_URL（手元の clone のパスでもよい）。ないときは https を試し、取れなければ ssh で取る
# （CI は https にトークンを差し込み、手元の PC は ssh の鍵で読むため）
set -euo pipefail
root="$(cd "$(dirname "$0")/.." && pwd)"
ref="$(tr -d '[:space:]' < "$root/OCTANTSPECS_REF")"
dir="$root/.octantspecs"
https_url="https://github.com/BeatOctant/OctantSpecs.git"
ssh_url="https://github.com/BeatOctant/OctantSpecs.git"
if [ ! -d "$dir/.git" ]; then
  if [ -n "${OCTANTSPECS_URL:-}" ]; then
    git clone --quiet "$OCTANTSPECS_URL" "$dir"
  elif ! GIT_TERMINAL_PROMPT=0 git clone --quiet "$https_url" "$dir" 2>/dev/null; then
    rm -rf "$dir"
    echo "https で取れなかったので ssh で取ります" >&2
    git clone --quiet "$ssh_url" "$dir"
  fi
fi
GIT_TERMINAL_PROMPT=0 git -C "$dir" fetch --quiet origin "$ref"
git -C "$dir" checkout --quiet --detach FETCH_HEAD
echo "OctantSpecs $ref ($(git -C "$dir" rev-parse --short HEAD)) -> $dir"
