#!/usr/bin/env bash
set -euo pipefail
root=$(cd -- "$(dirname -- "${BASH_SOURCE[0]}")/.." && pwd)
if [[ ${1:-} != --approved ]]; then
  echo 'Downloading reference sources requires approval. After approval, run with --approved.' >&2
  exit 2
fi
mkdir -p "$root/reference/src"
while read -r name revision repository; do
  [[ -z "$name" || "$name" == \#* ]] && continue
  path="$root/reference/src/$name"
  if [[ ! -d "$path/.git" ]]; then
    git init -q "$path"
    git -C "$path" remote add origin "$repository"
    git -C "$path" fetch -q --depth 1 origin "$revision"
    git -C "$path" checkout -q --detach FETCH_HEAD
  fi
  [[ $(git -C "$path" rev-parse HEAD) == "$revision" ]] || { echo "Unexpected revision: $name" >&2; exit 1; }
  [[ -z $(git -C "$path" status --porcelain) ]] || { echo "Modified reference source: $name" >&2; exit 1; }
done < "$root/scripts/heif-reference-versions.tsv"
