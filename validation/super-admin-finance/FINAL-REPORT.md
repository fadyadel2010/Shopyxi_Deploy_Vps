# Super Admin Finance forensic audit

**Status: ACCEPTED for the empty production CommerceHub scope.**
Deployed 2026-11-17 UTC; post-release database preservation, authorization,
read-only production checks, and real headed-browser Arabic/English acceptance
all passed. This is **not** a certification of live marketplace transaction
lifecycles, because production CommerceHub contains no orders, payments,
refunds, or financial ledger entries at all.

- Branch: `finance/super-admin-forensic-audit-20261117`
- Clean starting commit: `3a124e6`
- Application commits: `9021cfc`, `c5a18b5`
- Deployed source commit: `c5a18b5804c794465a6af8746787696c214a859b`
- Deployment receipt: `deployment-receipt.json`
- Acceptance binding: `release-acceptance.json` (`PASS_EMPTY_PRODUCTION_SCOPE`)

## 1. Executive summary

Production Finance showed all zeros. Read-only SQL independently established
that this is genuine: production has **no CommerceHub marketplace orders,
sub-orders, payments, refunds, or operational ledger entries at all**, including
non-deleted and soft-deleted records. The zeros alone were therefore not a
dashboard defect, but the audit still found and fixed accounting and reporting
defects that would have produced wrong results once real marketplace activity
exists.

The release was delivered as narrow API/Web overlays built from the exact
production parent images. Only the changed components were deployed. No
migration ran, no financial or business-data write occurred, and all 15
inspected production state scopes retained identical counts and fingerprints.

POS invoices are a separate accounting population and were not folded into the
CommerceHub commission KPIs. Their independent reconciliation remained clean:
3 invoices, all fully returned, with matching revenue/COGS journals and
reversals.

## 2. Defects reproduced and remediated

| Finding | Correction | Historical impact |
|---|---|---|
| Promo-funded full refunds left residual platform revenue | Debit gross commission; separately restore promo expense; align decision and reconciliation expectations | Append-only future posting; no historical rewrite |
| Fully funded zero net commission fell back to gross | Preserve the frozen gross/promo split, including a legitimate zero | No current-rule repricing |
| Final partial refund could reverse the original full commission again | Cumulative before/after quantity and commissionable-principal allocation | No edits to completed refunds |
| Independently rounded partial allocations lost cents | Allocate promo from rounded gross entitlement; net = gross − promo | Conserves frozen totals to the cent |
| Zero-rate recognized orders disappeared from count/AOV | Canonical payment-credit recognition when the frozen commission is zero | No fabricated commission rows |
| Multiple promo usages multiplied reporting joins | Deterministic single non-deleted usage join; duplicate diagnostics retained | No deletion of duplicate evidence |
| Refund-only daily promo recovery was clamped away | Keep signed daily promo cost | Derived projection only |
| Partial-day windows could read whole-day caches | Cache only exact complete UTC days | Read-path correction |
| Daily distinct refunds were summed across dates | Window-distinct, source-backed recognized cohort | No browser-side money recomputation |
| Refund rate mixed unrelated populations | Numerator limited to recognized-in-window orders refunded in-window | Explicit bounded cohort |
| Global integrity double-counted diagnostics | Count each diagnostic category once | Diagnostics only |
| Empty/limited scans looked like comprehensive approval | `NO_RECORDS`, explicit limited coverage, localized labels, “Not checked” | No ledger mutation |
| Later-recognized older orders escaped scoped source checks | Scope existing ledger recognition time with creation fallback for missing-ledger checks | Read-only |
| Missing/error frontend data appeared as financial zero or stale PASS | Unavailable placeholders, retry, stale-result warning, unavailable integrity badge | Presentation only |

The detailed authority and formula contract is in `ACCOUNTING-MAP.md`. The
refund correction posts gross commission reversal and separately restores promo
expense, while the persisted refund entity retains the existing net reversal and
promo reversal fields. No migration was required.

## 3. Validation receipts

| Check | Result | Receipt |
|---|---|---|
| Connected PostgreSQL backend suite | **176/176 passed**, none skipped | `test-results/backend-final.trx` |
| Focused Finance frontend | **13/13 passed** | `frontend-tests.json` |
| All Super Admin frontend | **148/148 passed** in 33 files | `frontend-super-admin-tests.json` |
| Full frontend TypeScript check | Passed | build log / `FINAL-REPORT.md` |
| Production Web build | Passed; existing bundle-size warning remains | build log |
| API Release build | Passed, 0 errors | build log |

Coverage includes real commission resolution, online and COD refunds,
promotions, full platform funding, zero commission, post-payout refunds,
retries, GL bridge, frozen cent conservation across partial refunds, cohort
boundaries, late recognition, outbox coupling, authorization, and refund-scope
checks. Production accounting-period guards were not weakened.

One pre-existing source-string certification expected an obsolete Owner/Admin
attribute on authenticated legacy refund tombstones. It now tests the actual
authenticated, HTTP-410-disabled mutation contract. Production authorization and
disabled mutation handlers were unchanged.

## 4. Release construction and immutable identity

Parents were exported from the running production deployments and verified
before overlay construction. Only three API assemblies and the built Web assets
were replaced.

| Component | Parent image | Deployed image (manifest) | Config digest | Archive SHA256 |
|---|---|---|---|---|
| API | `docker.io/shopixy/api@sha256:cb900c183069f94311142ad9c5a16fa23266611d3c29f674bae64040189ac307` | `localhost/shopixy/api@sha256:5a3bab31b628f8a3dfebd88c9eb0d60083176f9fc5f0b8a4d7c43845844e67ba` | `sha256:29a4c811c107791767f3bb86705c87084417df2b421e6e9b1598b30fd590b5bb` | `0809a79bfa4b4885836724e833dece48de960a771095df5b2968e124709b84b5` |
| Web | `docker.io/shopixy/web@sha256:627af0b79e1a8bfa4be76691df0f43c938c9b586727d11c2f223e43580c21568` | `localhost/shopixy/web@sha256:207a1794c0411b5a895a9cb411b4b91836c779b3283bfea127cc7e888eb216bf` | `sha256:fc4363269b6dd62cc4d22b3875b738e8fa4e9ab362859a629c1d6531a75bd3d8` | `3a2bd3d190efc3f086cc2137ab3b1b3695a9ce5b5caa025861fe9c6125d6cac4` |

`runtime-parent-compatibility.json` now validates actual assembly references
instead of design-time NuGet entries:

- Parent and overlay runtime TFM match (`net10.0`).
- Exactly 3 overlay assemblies, with hashes identical to the validated build.
- 146 referenced assemblies; **0 missing** against the parent `/app` folder and
  installed shared frameworks; **0 app-local version raises**.
- The overlay does not replace `deps.json` or `runtimeconfig.json`.
- The extra Release `deps.json` entries (EF Design, Roslyn, Build, Composition,
  Humanizer, etc.) are `PrivateAssets=all` with compile/runtime assets excluded;
  they cannot appear in an assembly reference table and were not copied.

`release-inputs.json` binds source commit, artifact hashes, archives, and image
digests.

## 5. Deployment

The deployment runner imported each OCI archive into the production containerd
store, verified the tag config digest, tagged and verified the manifest digest,
patched only the authorized container image with Kubernetes JSON-patch `test`
guards on the old resource version and old image, waited for rollout, verified
pod image identity and runtime file hashes, and checked every unrelated
deployment's spec against the committed predeploy baseline.

**Disclosed runner incident.** The first run patched and fully rolled out the
API, then aborted before Web because the runner compared CRI `imageID` to the
image *config* digest; containerd reports the *manifest* digest there. No
rollback or unrelated change occurred. `resume-deploy.py`:

1. Reconstructed the API pre-change spec by restoring the captured parent image
   string and proved its SHA256 equals both `beforeSpecSha256` and the committed
   predeploy baseline (`df93ad…`), so only the image field changed.
2. Verified all three API runtime assembly hashes on the running pod.
3. Deployed and verified Web with the same guards.
4. Proved the other four deployments are spec- and UID-identical to the
   predeploy baseline.

The obsolete assertion in `deploy-release.py` was corrected to accept the
manifest digest in CRI `imageID`; the receipt preserves the incident and the
resumed verification timestamps. The receipt itself is left at its generated
`DEPLOYED_RUNTIME_VERIFIED_BROWSER_PENDING` state; `release-acceptance.json`
adds the independent post-release browser, integrity, preservation, and
authorization criteria plus the scope statement.

### Post-deployment runtime verification

- `shopixy-api` 1/1 ready; pod 2/2 running, 0 restarts.
- `shopixy-web` 1/1 ready; pod 1/1 running, 0 restarts.
- Running image IDs match the deployed manifest digests.
- `/app/PointOfSale.API.dll`, `PointOfSale.Core.dll`, and
  `PointOfSale.Infrastructure.dll` hashes match the validated build exactly.
- Web `index.html` and `assets/index-eZGOKluk.js` hashes match the validated
  build exactly.
- `shopixy-baileys`, `shopixy-commercehub`, `shopixy-r2-egress`, and
  `shopixy-storefront` remained unchanged.
- No migration executed. No business-data write executed.
- Images are stored in production containerd and are not registry-published;
  the source archives remain in `/var/tmp/shopixy-finance-superadmin-20261117`.

## 6. Post-release preservation and authorization

`preservation-after.json` compared 15 scopes against the predeploy baseline.
Every count and database-computed MD5 fingerprint matched:
`allInspectedStatePreserved: true`, `changedScopes: []`.

Key unchanged state includes 3 SalesInvoices, 5 Returns, 26 ledger journals,
60 ledger lines, 1 commission rule, 3 variant stock rows, 7 variant price rows,
and 1 listing publication row. All CommerceHub financial, refund, earning,
payout, settlement, and promo scopes remained at their prior zero counts.

`anonymous-authorization-after.json`: 10 Finance endpoints probed without
credentials; **10/10 returned HTTP 401**, and none returned a financial payload.
The pre-existing Super Admin authorization path was not modified.

`production-after.json` re-verified:

- All-time CommerceHub orders, sub-orders, payments, refunds, and ledger
  entries: 0.
- Global 10% rule present and verified; 0 active overrides.
- 0 unsupported ledger currencies, orphan order/refund ledger entries,
  duplicate idempotency groups, or over-refunded sub-orders.
- All deployments fully ready.

## 7. Real headed-browser acceptance

The user personally authenticated the dedicated headed Chrome profile. No
cookie/token was read, exported, or injected, and no login automation was used.
`browser-after.json` records:

- **8/8 screenshots**: English/LTR and Arabic/RTL at 1440px and 390px, default
  window and historical **2026-10-18 → 2026-11-16**.
- No horizontal overflow, no visible error state, and correct `lang`/`dir`.
- The historical window inputs held exactly the requested dates.
- The deployed bundle `assets/index-eZGOKluk.js` was the bundle actually loaded
  (cache disabled).
- Six required Finance endpoints returned HTTP 200; no failed requests.
- Overview metrics were all zero with `dataSource: LIVE_LEDGER`.
- 0 console errors, 0 exceptions, 0 non-auth business writes.

`browser-integrity-after.json` additionally captures the integrity card in both
languages at 1440px:

- API `integrity-check` returned HTTP 200, `status: NO_RECORDS`,
  `includesGlobalLedgerChecks: false`, 0 flags, and all counters 0.
- Both languages display “No records checked” / “لم تُفحص سجلات”, the explicit
  limited-coverage notice, and 3 “Not checked” / “لم يُفحص” counters.
- No raw `NO_RECORDS`/`ISSUES_DETECTED`/`NOT_APPLICABLE` enum text and no
  integrity error.
- Account chrome was hidden only for the privacy of the evidence screenshots;
  the signed-in email is not retained in the images.

Screenshots: `finance-after-*.png` and `finance-after-integrity-*.png`.

## 8. Scope limits and residual observations

1. **Empty production marketplace scope.** CommerceHub is empty all-time.
   Acceptance proves that the deployed release reports this truthfully and that
   deployment, authorization, and Arabic/English presentation work on
   production. It cannot certify live commission, promo, refund, settlement, or
   payout lifecycles; those are covered by the PostgreSQL test receipts.
2. **POS is separate.** The three POS invoices and their returns were
   reconciled independently. They are not CommerceHub commission revenue and
   were not presented as such.
3. **Messaging warnings.** The API logs recurring MassTransit/RabbitMQ
   reconnect warnings from container start. `Program.cs`, RabbitMQ settings,
   health-check code, and the messaging package closure are unchanged by this
   release, and the read-only audit found no pending or permanently failed
   accounting outbox messages in the POS scope. This is an
   infrastructure/messaging observation, not a regression of this release and
   not a finance correctness failure; it is recorded rather than hidden.
4. **Web bundle-size warning** remains, as pre-existing.
5. **Registry durability.** The new images are digest-pinned in production
   containerd, not published to a registry. Re-import from the retained archives
   would be required if the node's image store were lost.

Rollback is a single image-field patch per deployment back to the verified
parent digests listed in section 4; no database rollback is involved.

## 9. Evidence index

| File | Contents |
|---|---|
| `release-inputs.json` / `deployment-receipt.json` | Source commit, artifact/archive hashes, image identities, spec hashes, readiness, runtime hashes |
| `release-acceptance.json` | Acceptance criteria, evidence file hashes, PASS scope statement |
| `runtime-parent-compatibility.json` | Three-assembly overlay binding proof |
| `test-results/backend-final.trx` | 176/176 backend tests |
| `frontend-tests.json`, `frontend-super-admin-tests.json` | 13/13 and 148/148 frontend tests |
| `production-predeploy.json`, `production-after.json` | Read-only DB/workload evidence before and after |
| `preservation-predeploy.json`, `preservation-after.json` | 15-scope count/fingerprint comparison |
| `anonymous-authorization-after.json` | 10/10 anonymous 401 probes |
| `browser-before.json`, `browser-after.json` | Headed-browser baseline and acceptance |
| `browser-integrity-after.json` | Truthful limited-coverage / `NO_RECORDS` presentation |
| `finance-after-*.png`, `finance-after-integrity-*.png` | English/Arabic, 1440/390, default/historical captures |
| `production-pos-before.json` | Separate POS reconciliation |
| `evidence-secret-scan.json` | Evidence publication secret scan |
| `ACCOUNTING-MAP.md` | Authority, formulas, refund contract, integrity limits |

## 10. Acceptance statement

For source commit `c5a18b5804c794465a6af8746787696c214a859b`, the API and Web
components were built from the exact running production parents, deployed with
only their authorized image fields changed, bound to immutable manifest digests,
and verified at runtime. Production financial history, balances, settlement
outcomes, commission rules, prices, stock, publication state, unrelated
workloads, and persistent data were preserved. Anonymous access remained
denied. Real headed-browser Arabic and English acceptance passed at desktop and
mobile widths, including the historical reporting window and truthful integrity
coverage.

**Release accepted for the empty production CommerceHub scope.** Live
marketplace transaction certification remains outside what this production
dataset can prove.
