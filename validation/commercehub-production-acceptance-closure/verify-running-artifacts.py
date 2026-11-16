import datetime
import hashlib
import json
import subprocess
from pathlib import Path

OUT = Path(__file__).resolve().parent


def remote(command, data=None):
    return subprocess.run(["ssh", "-o", "BatchMode=yes", "shopixy-vps", command],
                          input=data.encode() if data is not None else None,
                          capture_output=True, check=True).stdout.decode()


def pod(app):
    return json.loads(remote("kubectl get pods -n shopixy-app -l app=" + app + " -o json"))["items"][0]["metadata"]["name"]


web_pod = pod("shopixy-web")
raw = remote("kubectl exec -i -n shopixy-app " + web_pod + " -- sh",
             "cd /usr/share/nginx/html\nfind . -type f -print0 | sort -z | xargs -0 sha256sum\n")
actual = {line.split(None, 1)[1].strip().removeprefix("./"): line.split()[0] for line in raw.splitlines()}
inputs = json.loads((OUT / "artifact-inputs.json").read_text())
expected = {file["path"]: file["sha256"] for file in inputs["files"]}
assert actual == expected
api_pod = pod("shopixy-api")
api_hashes = remote("kubectl exec -n shopixy-app " + api_pod +
                    " -c api -- sha256sum /app/PointOfSale.API.dll /app/PointOfSale.Core.dll /app/PointOfSale.Infrastructure.dll")
old = json.loads((OUT.parent / "commercehub-production-deployment/production-postdeploy-verification.json").read_text())
previous = {entry["file"]: entry["sha256"] for entry in old["apiAssemblyParity"]}
api_actual = {Path(line.split()[1]).name: line.split()[0] for line in api_hashes.splitlines()}
assert api_actual == previous
before = json.loads((OUT / "runtime-before.json").read_text())
after = json.loads((OUT / "runtime-after.json").read_text())
assert before["sql"] == after["sql"]
for name in ("shopixy-api", "shopixy-storefront"):
    assert next(w for w in before["workloads"] if w["name"] == name) == next(w for w in after["workloads"] if w["name"] == name)
result = {
    "verifiedAtUtc": datetime.datetime.now(datetime.timezone.utc).isoformat(),
    "webPod": web_pod, "webFilesCompared": len(actual), "webDistParity": True,
    "apiPod": api_pod, "apiAssemblyHashes": [{"file": name, "sha256": value} for name, value in api_actual.items()],
    "matchesAcceptedApiBuild": True,
    "commissionListingSourceVariantSqlUnchanged": True, "apiWorkloadUnchanged": True,
    "storefrontWorkloadUnchanged": True,
}
(OUT / "running-artifact-parity.json").write_text(json.dumps(result, indent=2) + "\n")
print(json.dumps(result, indent=2))
