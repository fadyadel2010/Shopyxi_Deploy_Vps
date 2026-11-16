set -euo pipefail
export DOCKER_HOST=unix:///run/user/1000/shopixy-docker.sock
export DOCKER_CONFIG=/home/fadya/shopixy-toolchain/config/docker
D=/opt/shopixy-toolchain/bin/docker
BUILD=/mnt/c/Users/fadya/AppData/Local/Temp/opencode/commercehub-closure-20261117-856648d
IMAGE=shopixy/web:commercehub-closure-856648d-20261117
PARENT=shopixy/web:commercehub-publishing-4fb2beb-20261116
test "$($D image inspect "$PARENT" --format '{{.Id}}')" = sha256:e95bbdf5a645495003a3f88a580148238d738b6b1fd0ee345194d9e271de02a8
$D build --network=none --pull=false -t "$IMAGE" "$BUILD/web-context"
test "$($D image inspect "$IMAGE" --format '{{index .Config.Labels "shopixy.io/source-commit"}}')" = 856648d0b61a7621c600524b5aa55cf4a72fb385
test "$($D run --rm --network=none --entrypoint /usr/bin/id "$IMAGE" -u)" = 101
$D run --rm --network=none --entrypoint /usr/sbin/nginx "$IMAGE" -t
$D run --rm --network=none --entrypoint /bin/sh "$IMAGE" -ec 'cd /usr/share/nginx/html; find . -type f -print0 | sort -z | xargs -0 sha256sum' > "$BUILD/image-dist-sha256.txt"
(cd "$BUILD/web-context/dist" && find . -type f -print0 | sort -z | xargs -0 sha256sum) > "$BUILD/build-dist-sha256.txt"
diff -u "$BUILD/build-dist-sha256.txt" "$BUILD/image-dist-sha256.txt" > "$BUILD/dist-parity.diff"
/opt/shopixy-toolchain/bin/trivy image --skip-db-update --skip-java-db-update --offline-scan --scanners vuln --severity HIGH,CRITICAL --exit-code 1 --format json --output "$BUILD/web-trivy.json" "$IMAGE"
$D save -o "$BUILD/web.tar" "$IMAGE"
$D image inspect "$IMAGE" > "$BUILD/image-inspection.json"
sha256sum "$BUILD/web.tar"
