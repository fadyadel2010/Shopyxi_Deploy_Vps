# SHOPYXI Marketing

Standalone, bilingual static site. `/` temporarily redirects to `/ar/`; twelve
English/Arabic routes remain directly addressable. Cairo and genuine synthetic-data
product captures are local assets. React renders at build time only.

## Install and qualify

Use Node 22 and the committed lockfile, in a clean checkout:

```sh
npm ci
npm run build
npm test
node --test tests/arabic-default.test.mjs tests/screenshots.test.mjs
node tests/font-inspect.mjs
npm run test:browser
npm run test:public:browser
node scripts/seal-qualification.mjs test-output/public/public-browser-results.json
npm run build:public
npm run test:public
```

Install the exact locked Playwright browsers with `npx playwright install` when
not already provisioned. Qualification requires Chromium, Firefox and WebKit,
180 responsive states, 72 axe audits and no failures, incomplete audits, external
requests or console/network errors. Reports/screenshots stay in ignored
`test-output/` or `MARKETING_EVIDENCE_DIR`, not in Git. Review builds are always
nonindexable, including under NODE_ENV=production. Never bypass `build:public`.

`release-approval.json` records the actual user authorization and scope;
`CLAIMS-EVIDENCE.md` and `src/content/publication.json` distinguish source review
from the historical multi-reviewer PENDING register. Unsupported free-offer and
consultation wording does not ship. Source/asset/script/test/packaging/lockfile
changes invalidate qualification. LF checkout rules make hashes portable.
Approval hashes must be reviewed and updated before qualification; there is no
automatic approval command. Reviewers other than the user are not presumed.

## Production image and existing VPS delivery

`Dockerfile` is a pinned multi-stage public build with UID/GID 101 runtime.
Only verified compiled files and `nginx.conf` are copied into the final stage;
no source, npm dependencies, reports, internal receipts or secrets ship.
The existing daemonless workflow is also supported by `container/build-image.py`:

```sh
python container/build-image.py --source-commit <clean-committed-SHA> --output <external-evidence-directory>
```

First provision the runtime with `python container/prepare-runtime.py --output
<external-evidence-directory>`. It uses a disposable container in the existing
containerd's dedicated Marketing build namespace, verifies standard Alpine package
signatures and installs the exact patched versions in `container/runtime-lock.json`.
No host OS package installation, host mounts, public listener or Kubernetes object
is created. Runtime files are normalized into a SHA-256 locked layer.

The image builder validates the public receipt, clean commit, runtime-layer lock
and every artifact hash, then writes a deterministic OCI archive. It does not run
Docker: it reproduces the runtime copy stage using the **already guarded public
artifact**. The pinned Alpine + stable Nginx runtime avoids known vulnerabilities
in the older nginx-unprivileged image and omits its unnecessary curl/XML/XSLT/Brotli
packages. Docker/BuildKit is an optional alternative, not a new registry or VPS
dependency. Keep vulnerability/configuration scan results outside Git. Never
deploy an unqualified or vulnerable candidate.

Production already uses OCI upload → remote archive hash check →
`k3s ctr -n k8s.io images import` → tag immutable digest reference →
CRI verification on every schedulable node. There is currently one node.
Use `localhost/shopyxi/marketing:<source-SHA>` and deploy only
`shopixy-app/deployment/shopyxi-marketing`, container `marketing`, by digest
with imagePullPolicy Never. Do not apply broad manifests.

Before rollout save Deployment JSON/YAML, UID/resourceVersion, image digest,
revision, Pod readiness/restarts, and the unrelated-resource specifications.
Verify the candidate on the node, including Nginx config, UID, private health
port 8081, public port 8080, host admission, routes, headers and file hashes.
Use a resourceVersion/UID/image-tested JSON patch of the existing image field.
Wait for rollout and verify exact artifact bytes through `https://shopyxi.com`,
all routes/assets, browser console/network, CSP, robots/sitemap and readiness.
`container/deploy-marketing.py --evidence <image-evidence-directory>` automates
node digest checks, isolated preflight, guarded image-only patch, rollout, live
artifact/three-engine verification and automatic image rollback on failure.

On critical failure, patch **only** Marketing back to the saved immutable image,
wait for rollout and verify the old site is healthy. Keep old image/tag, snapshots,
OCI provenance and failure evidence. Never remove the previous image.

## Runtime contract and scope

Nginx retains self-only CSP/no connections/no forms, security headers,
no-transform/revalidated HTML, immutable hashed CSS/JS, revalidated fonts/images,
real 404/405, exact `shopyxi.com` host admission and private `/healthz` on 8081.
No public health route is introduced: the existing Traefik private-path rule
intentionally denies public `/healthz`. `www` routing is owned elsewhere and is
unchanged. No Service, ingress, DNS, TLS, middleware, database, business API,
registration submission, tracking or other application is modified.

Automated accessibility checks are not universal conformance, physical-device,
screen-reader, native-editor or third-party approval.
