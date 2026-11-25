# SHOPYXI Marketing — Phase 1H source, review only

Independent bilingual static React package. It is not merged into Admin,
Storefront, CommerceHub or any other application. It has no runtime business API,
forms, tracking, remote fonts, account integration or customer-session dependency.

## Release status

**Public release remains BLOCKED.** Phase 1H qualifies the local Marketing review
candidate with documented warnings, not an overall end-to-end/public release.
The registration destination had external Cloudflare-beacon CSP diagnostics;
56 source claims remain PENDING, and historical approval/qualification files do
not bind the redesigned source. Those files, CSP, claims and release checks are
preserved unchanged. Synchronizing this package is not publication approval.

## Isolated review

Use Node 22+ and the committed exact-version manifest/lockfile. Dependency
provisioning and browser installation are separate local setup operations:

```powershell
npm ci --ignore-scripts
npm run build
npm test
node --test tests/arabic-default.test.mjs tests/screenshots.test.mjs
node --test tests/release-guard.test.mjs
npm run test:browser
npm run preview
```

Browser tests require an already provisioned compatible Chromium. They serve only
an ephemeral loopback preview, block nonlocal requests and do not activate customer
destinations. Preview defaults to `http://127.0.0.1:4183/`; `/` redirects to Arabic.
The twelve localized routes are generated in review mode with noindex metadata.

`MARKETING_DIST` selects isolated output for the build, preview and main static/
browser suites. `MARKETING_EVIDENCE_DIR` selects disposable test evidence.
Some historical diagnostics explicitly read `dist/`; run them only in a copied
package, never against a protected working copy. `tests/independent-build.mjs`
also writes build output and is a historical helper, not the reconciliation runner.

## Included and excluded material

Current source, tests, build/preview helpers, nginx configuration, unchanged release
records, exact lockfile and the 25 currently required public assets are included.
The two immutable claim registers and original logo under `../docs/` are required
test fixtures. Cairo retains its SIL OFL license. Product screenshots are actual
application captures with synthetic data and unchanged provenance/hash records.

Historical editorial masters, superseded captures, import/comparison helpers that
require historical evidence, prior review reports, browser evidence, `dist/`,
`node_modules/`, caches and local environment files are not synchronized. They
remain protected in the original working copy; their exclusion does not authorize
deletion. Historical provenance paths are retained as historical records, not
claimed to be newly reproduced evidence.

The current public-static assertions and typography diagnostic have documented
unresolved results; do not weaken their assertions or turn review output into a
public artifact to claim success.

## Production stop line

`build:public` is fail-closed and requires actual scoped approval plus source-bound
technical qualification. Do not edit gates to bypass it. No public build, image
build/import, Kubernetes change, DNS/configuration change or Production deployment
is authorized by this source reconciliation. A separate review/request is required.
