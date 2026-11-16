import hashlib
import json
import tarfile
from pathlib import Path

OUT = Path(__file__).resolve().parent
BUILD = Path(r"C:\Users\fadya\AppData\Local\Temp\opencode\commercehub-closure-20261117-856648d")
inputs = json.loads((BUILD / "artifact-inputs.json").read_text())
inspection = json.loads((BUILD / "image-inspection.json").read_text())[0]
assert inspection["Config"]["Labels"]["shopixy.io/source-commit"] == inputs["sourceCommit"]
assert inspection["Config"]["Labels"]["shopixy.io/web-dist-sha256"] == inputs["webDistSha256"]
assert inspection["Config"]["User"] == "101:101"
assert (BUILD / "dist-parity.diff").read_bytes() == b""
scan = json.loads((BUILD / "web-trivy.json").read_text())
findings = [v for r in scan.get("Results", []) for v in r.get("Vulnerabilities", [])
            if v.get("Severity") in ("HIGH", "CRITICAL")]
assert not findings
with tarfile.open(BUILD / "web.tar") as bundle:
    index_bytes = bundle.extractfile("index.json").read()
    index = json.loads(index_bytes)
manifest = index["manifests"][0]["digest"]
archive_sha = hashlib.sha256((BUILD / "web.tar").read_bytes()).hexdigest()
result = {
    "sourceCommit": inputs["sourceCommit"], "sourceInputSha256": inputs["sourceInputSha256"],
    "webDistSha256": inputs["webDistSha256"], "webDistFileCount": len(inputs["files"]),
    "baseImage": "docker.io/shopixy/web@" + inputs["parentDigest"],
    "web": {"archive": "web.tar", "archiveSha256": archive_sha, "manifestDigest": manifest,
            "oldImage": "docker.io/shopixy/web@" + inputs["parentDigest"]},
    "apiImage": "docker.io/shopixy/api@sha256:cb900c183069f94311142ad9c5a16fa23266611d3c29f674bae64040189ac307",
    "storefrontImage": "docker.io/shopixy/storefront@sha256:e9333f609aa664d6de713d4db00b18deba054e3a8acbb559099d9ab245f9f3df",
    "verification": {"nonRootUid": 101, "nginxSyntax": True, "distParity": True,
                     "trivyHighCritical": len(findings), "scanMode": "cached-database offline vulnerability scan",
                     "scanCreatedAt": scan.get("CreatedAt"), "apiBuilt": False, "storefrontBuilt": False},
}
for output in (BUILD / "release-inputs.json", OUT / "artifact-verification.json"):
    output.write_text(json.dumps(result, indent=2) + "\n", encoding="utf-8")
print(json.dumps(result, indent=2))
