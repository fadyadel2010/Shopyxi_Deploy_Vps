# Release isolation

During final validation, the shared source directory acquired unrelated POS
branding edits (`main.jsx`, `Layout.jsx`, `Login.jsx`, design tokens and branding
documents). They were neither discarded nor included in this release.

The Orders work was copied by an explicit path allowlist into the independent
checkout `C:\Users\fadya\AppData\Local\Temp\opencode\orders-isolated-source-20261117`,
on the required `super-admin/orders-forensic-audit-20261117` branch at the exact
Finance starting commit. It has its own Git index and build outputs.
Initial frontend validation used shared dependency junctions. Those links
subsequently disappeared and the first committed-source Web build failed closed.
Web dependencies were therefore restored into this checkout with `npm ci` from
its committed lockfile, without changing versions. Cross-imported Storefront
types resolve through a junction to this checkout's owned Web dependencies.
The remote is the authorized GitHub repository, not the local clone source.

Final validation, source/artifact sealing, commits, packaging and delivery must
use this isolated checkout. Earlier receipts from the shared source remain
historical evidence, not final build authority.
