set -euo pipefail
export PATH="/opt/shopixy-toolchain/bin:$PATH"
export DOCKER_HOST=unix:///run/user/1000/shopixy-docker.sock
export DOCKER_CONFIG=/home/fadya/shopixy-toolchain/config/docker TRIVY_SKIP_VERSION_CHECK=true
base=/mnt/c/Users/fadya/AppData/Local/Temp/opencode/shopixy-storefront-20261114-baileys-pairing
e=/mnt/d/Shopixy-Automatic-Subdomains-Audit-R2-20261111/source/validation/storefront-20261114
test -s "$e/baileys-pairing-after.json"
old=shopixy/baileys:storefront-20261114-auth-scratch
tag=shopixy/baileys:storefront-20261114-pairing-watchdog
test "$(docker image inspect "$old" --format '{{.Id}}')" = sha256:7d1841c19ccdd8d654814b4f3a9d6420a655540103c21ad7bd90dcdfc9820e41
docker run --rm --network=none --entrypoint cat "$old" /app/dist/services/SelfHealingService.js > "$base/SelfHealingService.deployed.js"
diff -u "$base/SelfHealingService.deployed.js" "$base/SelfHealingService.before.js" > "$e/baileys-pairing-healing-parity.diff"
docker build --network=none --pull=false --build-arg "BASE_IMAGE=$old" --tag "$tag" "$base/context" > "$e/baileys-pairing-build.log" 2>&1
docker run --rm --network=none --entrypoint /bin/sh "$old" -ec 'cd /app; find . -type f -print0 | sort -z | xargs -0 sha256sum' > "$e/baileys-pairing-before-files.txt"
docker run --rm --network=none --entrypoint /bin/sh "$tag" -ec 'cd /app; find . -type f -print0 | sort -z | xargs -0 sha256sum' > "$e/baileys-pairing-after-files.txt"
grep -vE ' \./dist/services/(SessionManager|SelfHealingService|PairingSessionPolicy)\.js$' "$e/baileys-pairing-before-files.txt" > "$e/baileys-pairing-before-unchanged.txt"
grep -vE ' \./dist/services/(SessionManager|SelfHealingService|PairingSessionPolicy)\.js$' "$e/baileys-pairing-after-files.txt" > "$e/baileys-pairing-after-unchanged.txt"
diff -u "$e/baileys-pairing-before-unchanged.txt" "$e/baileys-pairing-after-unchanged.txt" > "$e/baileys-pairing-unrelated-parity.diff"
docker run --rm --network=none --entrypoint node "$tag" -e 'const p=require("/app/dist/services/PairingSessionPolicy");if(p.transitionalSessionTimeoutMs("qr_pending",30000)!==180000||p.transitionalSessionTimeoutMs("connecting",30000)!==30000)process.exit(1);console.log("Packaged watchdog policy: PASS")' > "$e/baileys-pairing-runtime-test.log"
trivy image --scanners vuln --severity HIGH,CRITICAL --exit-code 1 --format json --output "$e/baileys-pairing-trivy-high-critical.json" "$tag" > "$e/baileys-pairing-trivy.log" 2>&1
docker save "$tag" | gzip -1 > "$base/baileys-pairing.tar.gz"
docker image inspect "$tag" > "$e/baileys-pairing-image-inspect.json"
ref=$(docker image inspect "$tag" --format '{{index .RepoDigests 0}}')
echo "docker.io/$ref" > "$e/baileys-pairing-image-reference.txt"
echo "$tag" > "$e/baileys-pairing-image-tag.txt"
echo 'Pairing watchdog image gates passed; no SDK or dependency upgrade.'
