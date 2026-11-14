# Minimatch root security compatibility binding

Web and Storefront pin an **exact-parent-scoped** npm compatibility adjustment:

```json
"@typescript-eslint/typescript-estree@8.71.2": {
  "minimatch": "11.0.4"
}
```

This consumes the official patched minimatch release for GHSA-g3hv-75wg-m249 /
CVE-2026-118764. It is a locally validated compatibility binding, **not an
upstream-endorsed typescript-eslint release**. Do not broaden it to all minimatch
consumers or future parent versions. Official package source is not patched,
forked or vendored. Existing lint configurations, rule coverage and test selections
are unchanged.

## Compatibility basis

- typescript-estree 8.71.2 only calls the retained named API
  `minimatch(filePath, pattern, { dot: true })` in its project-service helper.
  It does not call removed `makeRe`, `Minimatch#regexp`, `AST`, `debug`,
  `matchOne` or `slashSplit` APIs. Its separate debug-package import and TypeScript
  AST types are not minimatch APIs.
- Official minimatch 11.0.4 supports Node `20 || >=22`; the verified toolchain is
  Node 22.20.0 with npm 11.21.1 and npm v3 lockfiles. Node 18 is not supported by
  this selected minimatch release. No package engine constraint is weakened.
- ESLint/config-array/eslintrc/React-plugin legacy minimatch 3.1.5 remains untouched;
  it is outside the corrected advisory range `>=7.0.0 <11.0.1`. Forcing version 11
  beneath every consumer would not be safe. eslint-plugin-import-x is absent;
  its published 4.17.1 still imports removed `makeRe` and is not introduced here.
- Isolated official-API, actual typed-project-service, golden matching,
  rule-equivalence, audit, build/type/lint and full project selections were checked
  before application. No DoS payload was executed.

## Reproducible verification

From **each** affected project root, use the committed lock convention:

```text
npm ci --ignore-scripts --no-audit --no-fund --engine-strict
npm run test:minimatch-compat
npm ls --all
npm audit
npm audit --omit=dev
npm run lint
npm run build
```

Also run that project's TypeScript `tsc --noEmit` and full Vitest selection.
The prior selections are Web 2,358 cases and Storefront 653 cases; repeated focused
or isolation runs must not be added as distinct tests. The shared guard checks the
exact parent/child versions, retained API, 380 fixed benign matching pairs against
pre-change golden outcomes (Windows and POSIX), and parser smoke behavior. It reads
the real helper in a disposable VM and never modifies installed package source.
It has no replacement-argument bypass. Run it in the repository layout, which also
contains shared theme-authority/configuration inputs.

## Maintenance and removal plan

Track typescript-eslint issue 13125 and its pending minimatch-11 work. Once an
official supported stable parent admits a patched minimatch, remove this exact
override and upgrade the coordinated parser/plugin chain only as necessary.
Remove or adapt the transitional version-specific guard in the same reviewed
change. Repeat clean installs, full trees/audits, effective-rule/positive-negative
fixtures, types, lint, builds and full case selections. The guard deliberately fails
on a parent/version change; do not silence it or expand the qualifier blindly.

Retain upstream license/attribution files in installed official packages. Minimatch
and its nonrecursive matcher declare BlueOak-1.0.0; existing brace/balanced package
licenses are retained. No upstream code is redistributed in this guard.

## Evidence and deployment boundary

Evidence directory:
`packages-minimatch-root-remediation-20261105` under the APP.2 final-closure evidence
root. The final normal-candidate results and exact source/evidence hashes are in
that directory's decision and manifests. Existing SQL Governance PASS evidence is
preserved; this npm/docs/guard change does not rerun or alter SQL inputs.

This source/dependency remediation does **not** establish the final Docker image
or deployed VPS exposure. Those remain UNKNOWN/BLOCKED without exact current
offline build/image/deployment provenance. Historical APP.3 images and clean
production npm audits are not current final-image proof. No deployment, promotion,
image build or APP.3 authorization is made.
