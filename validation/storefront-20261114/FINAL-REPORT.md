# Shopixy Production QA — 14 November 2026 — sanitized recovery edition

Original evidence compiled 2026-11-14T03:16:38.636Z. Full original report/evidence remains unchanged locally. This edition preserves acceptance conclusions and release references without tenant/customer/payment details, business identifiers, financial rows, raw responses or screenshots. Original report SHA-256 is recorded in recovery provenance.

## Scope and outcome

All **36 management screens** completed read/safe-control checks in the actual headed Production browser. This is **not a blanket full-workflow PASS**. Selected synthetic checkout, payment-review, internal manual-shipment, unpublished CMS draft and supervised WhatsApp transport checks had separate evidence. EGP-only; no real transfer, money collection, physical dispatch/delivery, card/Paymob activation or invented-recipient contact was claimed.

The prior cleanup removed 122 individually SHA-256-verified duplicate DLL/PDB files, 225,614,736 bytes. Those are historical task measurements, not bytes reclaimed by the present recovery task. Unique source/work, caches, backups and historical evidence were retained.

## Nine corrected defects in the accepted release

1. Cart read contention maps exhausted bounded reads to HTTP409 without retrying checkout/payment commands.
2. Appearance preview uses EGP, not dollars.
3. WhatsApp auth scratch uses writable temporary storage; ambiguous filesystem failures do not delete Redis auth.
4. Pairing watchdog observes the reviewed grace interval rather than destroying a valid session.
5. Previously authorized Baileys-only public HTTPS egress was narrowly enabled; unrelated policies unchanged.
6. Previously authorized Redis ACL changes added only the approved WhatsApp prefixes; unrelated authority unchanged.
7. Manual-shipment cancellation honors server-populated actor validation metadata; submitted actor spoofing remains rejected/overridden.
8. Terminal orders advertise no fulfillment progression actions.
9. Cancellation publication uses the existing service-owned transactional outbox, not a pre-commit event.

Focused shipping/binding, terminal/active action, worker-boundary, checkout/cart and commit-boundary regressions passed as recorded in the original evidence. These are existing release claims, not tests rerun during repository recovery.

## Historical deployment at that report's acceptance

- API: `docker.io/shopixy/api@sha256:fcdc623d757ae9f2ddf2314b8b6b81b0c993d85240c846ebffd6206fd2e468c0` — **superseded later the same day by the General Ledger API fix**.
- Admin: `docker.io/shopixy/web@sha256:756879210827a50c84d8d42da66e2d6e2235907e1af33930a2a450cab0f46682`.
- Storefront: `docker.io/shopixy/storefront@sha256:e9333f609aa664d6de713d4db00b18deba054e3a8acbb559099d9ab245f9f3df`.
- Baileys: `docker.io/shopixy/baileys@sha256:fb31e748e0262895030f609b6cb2edd50ea2a1703d5d5e6010d01c6e1988d3c4`.

All relevant workloads were Ready and immutable identities verified in the accepted evidence. Use `docs/PRODUCTION-BASELINE.md` and the live recovery observation for **current** images, including the API successor, sidecar, CommerceHub and R2 egress.

## Actual acceptance versus unexecuted workflows

- Public catalog/product/cart/pricing/shipping/autosave and mobile EGP presentation were checked.
- Three explicitly synthetic orders ended Cancelled; synthetic manual-wallet proof was rejected, never treated as real money. The original internal manual shipment ended Cancelled, with no external pickup/dispatch/delivery/collection.
- Temporary synthetic product/stock/reservations/period were reconciled/restored using existing application-authoritative procedures; required original/reversal/audit history remained retained.
- A CMS draft persisted unpublished. No publication was claimed.
- WhatsApp pairing connected and one supervised self-addressed transport message was user-confirmed received. Messaging automation remained disabled. This was **not** an order-confirmation/OTP/payment/recovery workflow PASS or bulk-messaging approval.
- Cancellation commit-race coverage was local transactional regression; no extra Production order was manufactured to claim another full automatic-cancellation roundtrip.

**Not claimed:** real carrier registration/cancellation/dispatch/delivery, real wallet transfer/approval or money collection, card/Paymob, OAuth/provider activation, real marketing/conversion capture, publications/domain purchases, bulk customer messages or every mutating management-screen workflow. These require separate authorized/provider-backed acceptance.

The repository retains selected packaging/deployment scripts and exact release source manifests. Detailed historical evidence stays locally; it was not deleted because its image was superseded. The present GitHub recovery task changed no application functionality or Production settings and performed no additional QA campaign.
