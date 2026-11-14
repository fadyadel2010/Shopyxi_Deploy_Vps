set -euo pipefail
export PATH="/opt/shopixy-toolchain/bin:$PATH"
export DOCKER_HOST=unix:///run/user/1000/shopixy-docker.sock
export DOCKER_CONFIG=/home/fadya/shopixy-toolchain/config/docker TRIVY_SKIP_VERSION_CHECK=true
base=/mnt/c/Users/fadya/AppData/Local/Temp/opencode/shopixy-storefront-20261114-baileys
e=/mnt/d/Shopixy-Automatic-Subdomains-Audit-R2-20261111/source/validation/storefront-20261114
test -s "$e/baileys-baseline-parity.json"
test -s "$e/baileys-scratch-tests.json"
old=docker.io/shopixy/baileys@sha256:a396a42d133883cfe1a1a6c3cf1ab73f10920c3d3931c7c8cb106da601f48291
docker tag "$old" shopixy/baileys:storefront-20261114-certified-baseline
old=shopixy/baileys:storefront-20261114-certified-baseline
tag=shopixy/baileys:storefront-20261114-auth-scratch
docker build --network=none --pull=false --build-arg "BASE_IMAGE=$old" --tag "$tag" "$base/context" > "$e/baileys-build-v2.log" 2>&1
docker run --rm --network=none --entrypoint /bin/sh "$old" -ec 'cd /app; find . -type f -print0 | sort -z | xargs -0 sha256sum' > "$e/baileys-before-files.txt"
docker run --rm --network=none --entrypoint /bin/sh "$tag" -ec 'cd /app; find . -type f -print0 | sort -z | xargs -0 sha256sum' > "$e/baileys-after-files.txt"
grep -vE ' \./dist/services/(SessionManager|AuthScratchDirectory)\.js$' "$e/baileys-before-files.txt" > "$e/baileys-before-unchanged.txt"
grep -vE ' \./dist/services/(SessionManager|AuthScratchDirectory)\.js$' "$e/baileys-after-files.txt" > "$e/baileys-after-unchanged.txt"
diff -u "$e/baileys-before-unchanged.txt" "$e/baileys-after-unchanged.txt" > "$e/baileys-unrelated-parity.diff"
docker run --rm --network=none --read-only --tmpfs /tmp:rw,noexec,nosuid,size=16m --entrypoint node "$tag" -e 'const a=require("/app/dist/services/AuthScratchDirectory").authScratchDirectory; const fs=require("fs"); const p=a("qa-20261114",true); fs.mkdirSync(p,{mode:448}); fs.writeFileSync(p+"/scratch-test","synthetic-only"); console.log("Production read-only root + existing writable tmp: PASS");' > "$e/baileys-readonly-root-test.log" 2>&1
trivy image --scanners vuln --severity HIGH,CRITICAL --exit-code 1 --format json --output "$e/baileys-trivy-high-critical.json" "$tag" > "$e/baileys-trivy.log" 2>&1
docker save "$tag" | gzip -1 > "$base/baileys.tar.gz"
docker image inspect "$tag" > "$e/baileys-image-inspect.json"
ref=$(docker image inspect "$tag" --format '{{index .RepoDigests 0}}')
echo "docker.io/$ref" > "$e/baileys-image-reference.txt"
echo "$tag" > "$e/baileys-image-tag.txt"
sha256sum "$base/baileys.tar.gz" > "$e/baileys-archive-SHA256SUMS.txt"
echo 'Minimal Baileys image packaged, read-only root regression passed, dependencies unchanged.'
