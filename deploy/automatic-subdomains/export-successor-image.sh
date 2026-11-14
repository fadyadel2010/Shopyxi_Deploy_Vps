#!/usr/bin/env bash
set -euo pipefail

repo_root=$(cd "$(dirname "${BASH_SOURCE[0]}")/../.." && pwd)
docker_bin=${DOCKER_BIN:-docker}
reference_file="$repo_root/deploy/automatic-subdomains/successor-image-reference.txt"
archive=${1:-}

if [[ -z "$archive" ]]; then
  echo "usage: $0 /absolute/path/to/successor-image.tar.gz" >&2
  exit 64
fi

image_ref=$(cat "$reference_file")
if [[ ! "$image_ref" =~ ^shopixy/api@sha256:[0-9a-f]{64}$ ]]; then
  echo "invalid immutable successor image reference" >&2
  exit 65
fi

"$docker_bin" image inspect "$image_ref" >/dev/null
image_tag=$("$docker_bin" image inspect "$image_ref" --format '{{index .RepoTags 0}}')
if [[ ! "$image_tag" =~ ^shopixy/api:automatic-subdomains-[0-9a-f]{12}-r3$ ]]; then
  echo "invalid successor image tag" >&2
  exit 66
fi
mkdir -p "$(dirname "$archive")"
if [[ -e "$archive" || -L "$archive" ]]; then
  echo 'Refusing to overwrite an existing immutable image archive' >&2
  exit 67
fi
tmp="$archive.tmp.$$"
trap 'rm -f "$tmp"' EXIT
"$docker_bin" save "$image_tag" | gzip -1 -n > "$tmp"
mv "$tmp" "$archive"
trap - EXIT

archive_sha=$(sha256sum "$archive" | awk '{print $1}')
printf '%s  %s\n' "$archive_sha" "$(basename "$archive")" | tee "$archive.sha256"
printf '%s  %s\n' "$archive_sha" "$(basename "$archive")" \
  > "$repo_root/deploy/automatic-subdomains/successor-image-archive-sha256.txt"
