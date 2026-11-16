"""Run on the existing VPS: guarded Web image-only rollout, no config changes."""
import copy
import datetime
import hashlib
import json
import subprocess
import tarfile
from pathlib import Path

ROOT = Path(__file__).resolve().parent
INPUT = json.loads((ROOT / "release-inputs.json").read_text())


def command(args, timeout=180):
    process = subprocess.run(args, capture_output=True, text=True, timeout=timeout)
    if process.returncode:
        raise RuntimeError("Release command failed: " + args[0])
    return process.stdout


def kube(*args, timeout=180):
    return command(["kubectl", "--request-timeout=30s", *args], timeout)


def sha(value):
    return hashlib.sha256(json.dumps(value, sort_keys=True, separators=(",", ":")).encode()).hexdigest()


def state(name):
    deployment = json.loads(kube("get", "deployment", name, "-n", "shopixy-app", "-o", "json"))
    pods = json.loads(kube("get", "pods", "-n", "shopixy-app", "-l", "app=" + name, "-o", "json"))["items"]
    spec = deployment["spec"]
    return deployment, {
        "name": name, "generation": deployment["metadata"]["generation"],
        "observedGeneration": deployment["status"]["observedGeneration"],
        "specSha256": sha(spec), "readyReplicas": deployment["status"].get("readyReplicas", 0),
        "images": [c["image"] for c in spec["template"]["spec"]["containers"]],
        "pods": [{"name": p["metadata"]["name"], "uid": p["metadata"]["uid"],
                  "containers": [{k: c.get(k) for k in ("name", "ready", "restartCount", "imageID")}
                                 for c in p["status"]["containerStatuses"]]}
                 for p in pods if not p["metadata"].get("deletionTimestamp")],
    }


def ready(receipt):
    return (receipt["generation"] == receipt["observedGeneration"] and receipt["readyReplicas"] == 1 and
            len(receipt["pods"]) == 1 and all(c["ready"] for c in receipt["pods"][0]["containers"]))


assert not (ROOT / "deployment-started.json").exists(), "Never retry a started deployment blindly"
before = {}
raw_web = None
for component, image in (("api", INPUT["apiImage"]), ("web", INPUT["web"]["oldImage"]), ("storefront", INPUT["storefrontImage"])):
    raw, item = state("shopixy-" + component)
    assert item["images"][0] == image and ready(item)
    before[component] = item
    if component == "web":
        raw_web = raw

archive = ROOT / "web.tar"
assert hashlib.sha256(archive.read_bytes()).hexdigest() == INPUT["web"]["archiveSha256"]
with tarfile.open(archive) as bundle:
    index_bytes = bundle.extractfile("index.json").read()
    assert json.loads(index_bytes)["manifests"][0]["digest"] == INPUT["web"]["manifestDigest"]
wrapper = "sha256:" + hashlib.sha256(index_bytes).hexdigest()
alias = "docker.io/library/commercehub-closure-import@" + wrapper
command(["/usr/local/bin/k3s", "ctr", "images", "import", "--digests", "--index-name", alias, str(archive)], 600)
replacement = "docker.io/shopixy/web@" + INPUT["web"]["manifestDigest"]
try:
    inspection = json.loads(command(["/usr/local/bin/k3s", "crictl", "inspecti", replacement]))
except RuntimeError:
    listing = command(["/usr/local/bin/k3s", "ctr", "images", "list"])
    source = next(line.split()[0] for line in listing.splitlines() if INPUT["web"]["manifestDigest"] in line.split()[1:])
    command(["/usr/local/bin/k3s", "ctr", "images", "tag", source, replacement])
    inspection = json.loads(command(["/usr/local/bin/k3s", "crictl", "inspecti", replacement]))
labels = inspection["info"]["imageSpec"]["config"]["Labels"]
assert labels["shopixy.io/source-commit"] == INPUT["sourceCommit"]
assert labels["shopixy.io/web-dist-sha256"] == INPUT["webDistSha256"]
current, current_receipt = state("shopixy-web")
assert current_receipt["specSha256"] == before["web"]["specSha256"]
assert current["metadata"]["uid"] == raw_web["metadata"]["uid"]
expected_spec = copy.deepcopy(current["spec"])
index = next(i for i, c in enumerate(expected_spec["template"]["spec"]["containers"]) if c["name"] == "web")
expected_spec["template"]["spec"]["containers"][index]["image"] = replacement
pointer = "/spec/template/spec/containers/" + str(index) + "/image"
patch = [
    {"op": "test", "path": "/metadata/uid", "value": current["metadata"]["uid"]},
    {"op": "test", "path": "/metadata/resourceVersion", "value": current["metadata"]["resourceVersion"]},
    {"op": "test", "path": pointer, "value": INPUT["web"]["oldImage"]},
    {"op": "replace", "path": pointer, "value": replacement},
]
receipt = {
    "startedAtUtc": datetime.datetime.now(datetime.timezone.utc).isoformat(),
    "sourceCommit": INPUT["sourceCommit"], "before": before, "image": replacement,
    "runtimeConfigId": inspection["status"]["id"], "expectedImageOnlySpecSha256": sha(expected_spec),
    "changedComponents": ["Admin Web"], "configurationChanged": False, "infrastructureChanged": False,
}
(ROOT / "deployment-started.json").write_text(json.dumps(receipt, indent=2) + "\n")
args = ["patch", "deployment", "shopixy-web", "-n", "shopixy-app", "--type=json", "-p", json.dumps(patch)]
kube(*args, "--dry-run=server")
kube(*args)
kube("rollout", "status", "deployment/shopixy-web", "-n", "shopixy-app", "--timeout=360s", timeout=390)
after = {component: state("shopixy-" + component)[1] for component in before}
assert all(ready(value) for value in after.values())
assert after["api"] == before["api"] and after["storefront"] == before["storefront"]
assert after["web"]["specSha256"] == sha(expected_spec)
assert after["web"]["images"][0] == replacement
receipt.update({"after": after, "completedAtUtc": datetime.datetime.now(datetime.timezone.utc).isoformat(),
                "apiUnchanged": True, "storefrontUnchanged": True})
(ROOT / "deployment-verified.json").write_text(json.dumps(receipt, indent=2) + "\n")
print(json.dumps(receipt, indent=2))
