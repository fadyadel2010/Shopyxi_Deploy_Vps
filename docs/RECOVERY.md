# Recovery guide

## 1. Recover source safely

Use a **new** directory on a disk with sufficient free space; never reset, restore over, clean or delete the preserved Windows working copy.

```text
git clone --branch main https://github.com/fadyadel2010/Shopyxi_Deploy_Vps.git NEW_RECOVERY_DIRECTORY
```

Verify repository privacy/access, the remote commit and any published baseline tag before relying on it. Read `PRODUCTION-BASELINE.md`, `LOCAL-WORK.md`, the two sanitized final reports and `deploy/production-baseline/accepted-source-sha256.json`. Compare SHA-256 source bytes, not folder dates. The repository index contains accepted alternate inputs while the original working tree intentionally retains local differences and absent files. A fresh clone retrieves committed source, not those local omissions.

Retain lockfiles, all EF migrations/designers/model snapshots, `DB` scripts, static assets, `.config/minimatch-parent-compatibility.cjs` and `theme-authority`. Do not use a compiled VPS image as a complete source replacement.

## 2. Identify the current release before any action

The existing read-only access path is SSH alias `shopixy-vps` and Kubernetes namespace `shopixy-app`. This task successfully used Kubernetes GET and CRI inspection. Reconfirm current images/replicas and reconcile imported runtime aliases before comparing to the baseline. A newer live release is a stop condition, not permission to overwrite it.

`deploy/production-baseline/live-observation.json` preserves safe environment values/references, mounts, services, workload structure and runtime mappings. Literal environments outside a narrow non-secret allowlist, command/argument text and exec probe commands are deliberately redacted; ConfigMap contents are represented by per-key hashes only. It is an observation/template, **not an apply-ready restore backup**.

Private credentials, kubeconfig, TLS/SSH keys, Secret values, customer data and database/PVC contents must remain under the existing approved custodians. They cannot be recovered from Git and must not be added to Git.

## 3. Existing build/release material

- Latest API: `validation/general-ledger-final-fix/package-api.sh`, `deployment-driver.py`, `deploy-api.cjs`, `source-index.json`, focused runner and regression source.
- Predecessor API/shipping/atomicity and Admin/Baileys: specifically retained packaging and deployment scripts in `validation/storefront-20261114/`.
- Historical domain rollout: `deploy/automatic-subdomains/RUNBOOK.md`, `R2-AUDIT-AND-ROLLOUT.md`, manifests, additive migration/grant scripts and sealed inputs.
- Historical offline preparation: `deploy/app43b/CONFIGURATION.md`, `application.template.json`, renderer/input contract and Data Protection recovery notes.

These are actual existing scripts, not invented recovery commands. They contain machine-specific paths, predecessor assertions, offline toolchain/cache requirements, expected test/image-scan receipts and create-new/no-repeat guards. Not all detailed receipts are copied into Git; the full evidence remains locally. They are retained for an operator to review and adapt **under separate authorization**, not to rerun automatically from this checkout. This assignment ran no build, image import, rollout, infrastructure change or database action.

The latest API packaging overlays only API/Infrastructure DLL/PDB files on its exact accepted parent image. Its parent must be independently available and verified; compiling source alone does not recreate all retained base layers/configuration. Preserve the existing image archives rather than assuming registry availability.

## 4. Data and infrastructure recovery — requires verification

No database restore, PVC restore, node rebuild, Secret rotation, TLS-key recovery, Data Protection recovery or off-host image restore was verified in this task. **No commands for those actions are asserted here.**

`deploy/app43b/DATA-PROTECTION-RECOVERY.md` explicitly distinguishes local helper tests from unverified legacy/Production custody and restore coverage. Do not mistake that historical preparation for a proven Production disaster-recovery procedure. Its referenced recovery helper is not present in this D: source selection; that is a documented gap, not permission to invent a replacement or run its example command.

Before any authorized restoration, the responsible operator must establish the actual approved backups/recovery point, secret/key custody, exact Data Protection ApplicationName and retained decryptors, required volumes, migration state, existing infrastructure runbooks and a reviewed rollback path. Never generate a new ring, run migration Down, reset databases or change cluster configuration merely to make the application start.

## 5. Evidence and local work

Full historical QA evidence and original final reports remain locally. Git includes sanitized reports and selected source/release manifests, not browser cookies, authorization headers, financial rows, private payment details or bulk screenshots/logs.

See `LOCAL-WORK.md` before using the original Windows directory. A non-clean working tree there is intentional preservation, not a failed checkout to repair. The separate Load-Test workspace and its data were not merged into the main application source or removed.
