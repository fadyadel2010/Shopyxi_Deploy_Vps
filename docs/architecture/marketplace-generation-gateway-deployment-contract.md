# Generation gateway: isolated credential and admission boundary

Implementation/review input, **not activation or deployment authorization**. APP.2
leaves external writes, automatic recovery/cutover and the gateway disabled.

## Runtime boundary

The explicit `--generation-write-gateway` API executable mode exits into a separate
minimal host before application registrations, background jobs, initializers and
migrations. Its `SHOPIXY_GENERATION_GATEWAY_ENABLED` setting defaults to disabled.
It uses only explicit `SHOPIXY_GENERATION_GATEWAY_*` environment inputs: LISTEN,
CLIENT_CERT_SHA256, SERVER_CERT_PFX, SERVER_CERT_PASSWORD, POSTGRES, PROVIDER,
PROVIDER_USERNAME, PROVIDER_PASSWORD and PROVIDER_CA. No value is supplied here.
The host requires HTTPS, exact peer certificate pins and VerifyFull PostgreSQL.
It exposes only lookup/submit/verify of a SQL-pinned immutable operation. Callers
cannot supply arbitrary provider paths, bulk bodies, aliases or scripts.

Application processes use `MarketplaceGenerationRemoteBoundary` with mTLS and
**must never receive the provider update secret or gateway SQL secret**. Their
provider credential can read/refresh pinned Generation targets, but has no
Generation document mutation, delete, alias, mapping, pipeline or lifecycle grant.
Other index owners retain only their exact physical index grants. OpenSearch role
grants are additive: an admin/default/wildcard secondary role invalidates isolation.
Do not reuse an existing application or fixture administrator identity.

`MarketplaceGenerationSecurityPlan.GatewayRole` produces an exact-physical-target
review plan, not an installed role. The gateway has update, read/metadata and
refresh permission only, with no physical delete or index-management authority.
Its provider secret is mounted only into the isolated gateway process. Network
policy, secret ACLs, effective role mappings, alternate client credentials and
independent-client denial must be verified at Runner before release authorization.
No credentials, role mapping, network policy or live server is changed in APP.2.

The SQL ledger must be one authoritative, durable PostgreSQL primary. Automatic
multi-host fallback is rejected. Protocol mutation transactions force LOCAL
`synchronous_commit=on`; authority/pointer triggers do likewise. Admission refuses
recovery-mode servers or `fsync`/`full_page_writes` disabled. This does not certify
a DNS/VIP failover, a split-brain cluster, lossy asynchronous standby promotion,
storage that lies about durable flushes, or SQL backup/epoch rewind while gateway
permits exist. Such failover/restore requires independent lossless-ledger and
external-primary-fencing proof, or an offline fresh-target/credential transition.
These are deployment prerequisites, not reasons to lower a runtime safety check.

## Admission and drain proof

SQL control-row locking serializes every durable gateway attempt with authoritative
epoch advancement. Submitted/OutcomeUnknown state, exact sealed ticket, provider
binding, physical name/UUID, journal, generation, revision, payload digest and open
admission are required before the attempt commits. Only then can the fixed script
be sent. SQL triggers reject epoch regression, epoch advancement over hazardous
attempts, control deletion/truncation and pointer cutover over hazardous attempts.
Unfenced historical unknown attempts are not imported or forgotten.

Session loss, lease expiry and request timeout do not settle an attempt. An exact
retained content/version witness makes every identical delayed request a no-op.
A higher revision witness is accepted only from another immutable SQL operation,
after refresh and full content verification. It is recorded as refreshed
supersession, never as historical visibility of the old payload. Tombstones are
retained. No physical target name may be reused, and no lifecycle principal may
recreate/alter an admitted, active or referenced target concurrently.

A forever-unobservable request can intentionally freeze admission/takeover. The
implementation does not trade safety for availability: recovery may retry the
same immutable operation while its epoch/ownership admission is current, but it
cannot manufacture an epoch transition after ownership loss. An offline rebuild
or provider-assisted definite drain requires separate reviewed authorization.

Supervised ownership recovery uses `MarketplaceGenerationOwnershipRecovery`:
first perform bounded, lookup-only `ReconcilePendingAsync` pages, then attempt
successor acquisition. This path cannot submit provider mutations, including when
the old lease is still current. Exact verified applied/refreshed content can settle
the original operation and delivery ledger before takeover. Missing observations
stay unknown; a full page returns its cursor for continued reconciliation. The
unchanged SQL guard returns `AwaitingAuthoritativeOutcome` for hazardous attempts,
including legacy raw claims without recoverable identities. Such claims are never
relabelled as unsent or acknowledged. Other SQL/authorization failures propagate.
Explicit current-owner delivery retains its existing immutable idempotent replay
protocol; the ownership-recovery entrypoint is distinct and never enables it.

## Required final provider negatives

Verify gateway-only update authority; rejection of every other principal's bulk,
direct document, alias, reindex, delete and administrative mutation against the
physical Generation; no additive broad roles; no auto-create or lifecycle rights;
no same-name recreation; queued/in-flight request drain across SQL connection loss;
and exact full-source/tombstone visibility after refresh. Test the pinned provider
version and supported client, not an assumed cross-document Painless capability.
The staged fixture identity is OpenSearch 3.9.0; the source client is 2.3.0. No
running/deployed provider version is inferred from those offline identities.
