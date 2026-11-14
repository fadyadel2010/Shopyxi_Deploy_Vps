# Integration transport configuration

These settings are names only, not production credentials. Supply secrets through
the deployment's existing protected configuration mechanism. Do not commit them,
embed them in URLs, print connection objects, or bypass certificate verification.
No deployment or production configuration change is performed by this document.

The APP.4.3B production profile is **one node and exactly one API replica**.
See `deploy/app43b/CONFIGURATION.md` and `deploy/app43b/DATA-PROTECTION-RECOVERY.md` for the
retained local RWO ring, Recreate rollout, original discriminator/decryptor custody
and owner-supplied references. No RWX or multi-node infrastructure is required.

## RabbitMQ

- `RabbitMQ:Host`: `amqps://<broker>:5671`. Bare hosts and `amqp://` remain
  supported for existing local development, but are not production TLS settings.
- `RabbitMQ:VirtualHost`, `RabbitMQ:Username`, `RabbitMQ:Password`: existing names.
- `RabbitMQ:Tls:CaCertificatePath`: optional readable PEM CA bundle. Without it,
  platform trust is used. CA files must contain CA certificates, not leaf keys.
- `RabbitMQ:Tls:ServerName`: optional DNS identity/SNI override when a discovery
  or routing address differs from the certificate identity. It is still verified.
- `RabbitMQ:Tls:ClientCertificatePath` and `RabbitMQ:Tls:ClientPrivateKeyPath`:
  required absolute mounted PEM references for AMQPS. The matching strong private
  key and current clientAuth certificate are supplied to both bus and readiness.

Bus and readiness use the same parser and TLS policy. URI vhosts are preserved;
conflicting separate vhost values, embedded credentials and ignored URL components
are rejected. Topology, consumer registration and retry/outbox policy are unchanged.

## Search (pinned OpenSearch.Client 2.3.0)

- `Elasticsearch:Url`: HTTPS endpoint.
- `Elasticsearch:Username` and `Elasticsearch:Password`: configure both or neither.
  Credentials over plaintext HTTP are rejected.
- `Elasticsearch:CaCertificatePath`: optional PEM CA bundle; hostname verification
  is always retained. Without it, platform trust is used.

RabbitMQ/search validate TLS1.2+ where controlled by the Rabbit transport. Their
shared certificate policy checks serverAuth, validity, chain and platform hostname,
and **online revocation excluding the root**. Publish reachable CRL/OCSP information
for private issuing CAs; unknown/offline revocation fails closed. No revocation
disable switch or trust-all callback is provided. Public DER trust-anchor bytes
are retained in memory; native certificates/chains are disposed per validation.
PEM trust anchors are loaded at configuration construction; rotate by rebuilding
the client/application, not by mutating captured live certificate objects.

Disabled search still returns a failing in-memory client without reading CA files
or contacting the endpoint. Client-family compatibility does not certify every
endpoint/grant against live OpenSearch3.9.0. Preserve verified TLS and role boundaries.

## API Redis/Sentinel

`Redis:ClientCertificatePath` / `Redis:ClientPrivateKeyPath` and independently
`Redis:SentinelClientCertificatePath` / `Redis:SentinelClientPrivateKeyPath` are
required absolute PEM file references for their TLS planes. Existing independent
connection strings/ACLs, CA paths and verified SNI/hostname behavior remain.
StackExchange.Redis2.8.24 retains the certificate callbacks on cloned/discovered
data options. Cache/session/SignalR paths use the central owned connector.
Missing/expired/malformed identities fail safely; identity options never downgrade
to plaintext. Certificate/CA changes require a controlled one-instance restart;
never replace the production Data Protection wrapping identity as an mTLS shortcut.

## Baileys native Redis/Sentinel

Existing `REDIS_HOST`, `REDIS_PORT`, `REDIS_PASSWORD`, `REDIS_DB` remain available
for direct connections. New configuration:

| Variable | Meaning |
|---|---|
| `REDIS_TLS` | `true` to enable TLS; mandatory in production and Sentinel mode |
| `REDIS_USERNAME` | Named data-node ACL identity |
| `REDIS_CA_CERTIFICATE_PATH` | Optional data-node PEM trust bundle; otherwise Node trust |
| `REDIS_CLIENT_CERTIFICATE_PATH` | Required TLS clientAuth certificate PEM file reference |
| `REDIS_CLIENT_PRIVATE_KEY_PATH` | Required matching private-key PEM file reference |
| `REDIS_SERVER_NAME` | Optional verified data-node DNS identity/SNI |
| `REDIS_SENTINEL_NAME` | Sentinel master service name |
| `REDIS_SENTINEL_NODES` | JSON array of `{"host":"<sentinel>","port":26379}` objects |
| `REDIS_SENTINEL_USERNAME` | Separate named Sentinel ACL identity |
| `REDIS_SENTINEL_PASSWORD` | Separate Sentinel credential, never inferred from data password |
| `REDIS_SENTINEL_CA_CERTIFICATE_PATH` | Optional independent Sentinel PEM trust bundle |
| `REDIS_SENTINEL_CLIENT_CERTIFICATE_PATH` | Independent discovery client certificate PEM reference |
| `REDIS_SENTINEL_CLIENT_PRIVATE_KEY_PATH` | Matching discovery private-key PEM reference |
| `REDIS_SENTINEL_SERVER_NAME` | Optional independently verified Sentinel DNS identity/SNI |
| `REDIS_MASTER_POOL_SIZE` | Integer1–4; default2. Replica pool is fixed0 |

Supply both Sentinel name and seeds. Native node-redis6.3.1 owns discovery,
promotion and reconnection; no fallback to a hard-coded host and no custom failover
controller. RESP2 is explicit at the Sentinel level. Data and monitor sockets have
independent trust/credentials and retain Node's standard hostname checking.
Do not configure a common SNI name unless all relevant node certificates contain it;
otherwise omit overrides and issue certificates matching the discovered DNS names.

No key prefix, serializer, encryption/AES/scrypt/BufferJSON format, storage TTL,
rate-limit behavior or session policy is changed. Existing command consumers use
a shared structural command interface rather than casting Sentinel to a direct
client. Graceful close has a five-second deadline and force-closes on failure.

Node TLS uses normal chain/hostname checks; Node does not provide the .NET
policy's automatic online revocation behavior. Certificate lifecycle and revocation
requirements must be assessed separately; do not describe the two as identical.
All local integration tests must use artificial credentials/state. Never launch
WhatsApp sessions as a Redis connectivity test.
