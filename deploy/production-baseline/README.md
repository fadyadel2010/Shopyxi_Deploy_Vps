# Sanitized Production recovery observations

Captured read-only on 14 November 2026. No Production settings were changed.

- `live-observation.json`: actual workloads/images, safe deployment structures, service identities, ConfigMap key hashes and CRI mappings. Not apply-ready; private/unknown literals, commands, arguments and exec probes are redacted.
- `accepted-source-sha256.json`: layered latest API/Admin/Storefront/Baileys affected-input manifests, checked against staged Git blobs.
- `deployment-comparison.json`: local manifest ConfigMap content comparison and retained script/source provenance. No Secret or database export.

Read `../../docs/PRODUCTION-BASELINE.md` and `../../docs/RECOVERY.md` first. Existing `app43b` assets are offline historical preparation; `automatic-subdomains` assets record an older rollout. Neither overrides the live image identities in this directory.
