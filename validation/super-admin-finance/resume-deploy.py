"""Resume the guarded API/Web release after the CRI imageID assertion defect.

The initial deploy-release.py patched and fully rolled out shopixy-api, then
aborted before touching shopixy-web because containerd reports the manifest
digest (not the config digest) in CRI imageID.  No rollback was attempted.

This continuation proves, from the committed predeploy baseline, that the only
deployment change was the authorized image field, then deploys and verifies
shopixy-web with the same guards, and finally verifies that every unrelated
deployment is byte-for-byte unchanged in spec.
"""
import copy
import datetime
import hashlib
import json
import subprocess
from pathlib import Path

root = Path.cwd()
inputs = json.loads((root / "release-inputs.json").read_text())
baseline = {d["name"]: d for d in json.loads((root / "production-predeploy.json").read_text())["deployments"]}
receipt = json.loads((root / "deployment-receipt.json").read_text())

def command(args, input_text=None, timeout=300):
    process = subprocess.run(args, input=input_text, text=True, capture_output=True, timeout=timeout)
    if process.returncode:
        raise RuntimeError("Release command failed: " + " ".join(args[:3]))
    return process.stdout

def kube(*args, input_text=None, timeout=180):
    return command(["kubectl", "--request-timeout=30s", "-n", "shopixy-app", *args], input_text, timeout)

def seal(value):
    return hashlib.sha256(json.dumps(value, sort_keys=True, separators=(",", ":")).encode()).hexdigest()

def digest(path):
    h = hashlib.sha256()
    with path.open("rb") as stream:
        for block in iter(lambda: stream.read(1024 * 1024), b""):
            h.update(block)
    return h.hexdigest()

def deployment(name):
    return json.loads(kube("get", "deployment", name, "-o", "json"))

def assert_spec_matches_baseline(dep):
    name = dep["metadata"]["name"]
    if seal(dep["spec"]) != baseline[name]["specSha256"]:
        raise RuntimeError(f"{name} spec no longer matches the predeploy baseline.")

def artifact_paths(component):
    artifacts = inputs["components"][component]["artifactSha256"]
    if component == "api":
        return {name: "/app/" + name for name in artifacts}
    selected = [name for name in artifacts
                if name == "index.html" or (name.startswith("assets/index-") and name.endswith(".js"))]
    return {name: "/usr/share/nginx/html/" + name for name in selected}

def verify_runtime(component, row, pod):
    paths = artifact_paths(component)
    runtime = kube("exec", pod["metadata"]["name"], "-c", component, "--", "sha256sum", *paths.values())
    hashes = {line.split(None, 1)[1].strip(): line.split(None, 1)[0] for line in runtime.splitlines()}
    for name, path in paths.items():
        if hashes.get(path) != inputs["components"][component]["artifactSha256"][name]:
            raise RuntimeError("Running application artifact differs from validated build.")
    row["execRuntimeArtifactHashesMatched"] = True
    row["verifiedRuntimeFiles"] = list(paths)

def verify_pod(component, row):
    all_pods = json.loads(kube("get", "pods", "-o", "json"))["items"]
    current = [p for p in all_pods if not p["metadata"].get("deletionTimestamp") and
               any(c["name"] == component and c["image"] == row["newImage"] for c in p["spec"]["containers"])]
    if not current:
        raise RuntimeError("No running pod uses the released image digest.")
    pod = next(p for p in current if p["status"].get("phase") == "Running" and
               all(c.get("ready") for c in p["status"].get("containerStatuses", [])))
    status = next(c for c in pod["status"]["containerStatuses"] if c["name"] == component)
    image_id_digest = status["imageID"].split("@")[-1]
    if image_id_digest not in (row["manifestDigest"], row["configDigest"]):
        raise RuntimeError("Running pod image identity mismatch.")
    row["pod"] = pod["metadata"]["name"]
    row["runningImageId"] = status["imageID"]
    verify_runtime(component, row, pod)

receipt["resumedAtUtc"] = datetime.datetime.now(datetime.timezone.utc).isoformat()
receipt["resumeReason"] = (
    "Initial runner tested CRI imageID against the config digest; containerd reports the manifest digest. "
    "shopixy-api had already rolled out and was fully ready. This continuation re-verifies the reconstructed "
    "pre-change API spec against the receipt and committed predeploy baseline, verifies API runtime artifacts, "
    "then deploys Web. No rollback or unrelated change occurred."
)
receipt["components"]["api"]["resumedVerification"] = True
receipt["components"]["api"]["firstVerificationAtUtc"] = receipt["startedAtUtc"]
receipt["components"]["api"]["resumedVerificationAtUtc"] = receipt["resumedAtUtc"]

# 1. API: prove only the image field changed, using the pre-change image string.
api = deployment("shopixy-api")
api_row = receipt["components"]["api"]
if api["metadata"]["uid"] != api_row["deploymentUid"]:
    raise RuntimeError("API deployment UID changed.")
index = next(i for i, c in enumerate(api["spec"]["template"]["spec"]["containers"]) if c["name"] == "api")
if api["spec"]["template"]["spec"]["containers"][index]["image"] != api_row["newImage"]:
    raise RuntimeError("API deployment does not run the released image.")
reconstructed = copy.deepcopy(api["spec"])
reconstructed["template"]["spec"]["containers"][index]["image"] = api_row["oldImage"]
if seal(reconstructed) != api_row["beforeSpecSha256"] or seal(reconstructed) != baseline["shopixy-api"]["specSha256"]:
    raise RuntimeError("API changed beyond the authorized image field.")
api_row["reconstructedBeforeSpecSha256"] = seal(reconstructed)
api_row["afterSpecSha256"] = seal(api["spec"])
api_row["readyReplicas"] = api["status"].get("readyReplicas", 0)
if api_row["readyReplicas"] != api["spec"]["replicas"]:
    raise RuntimeError("API deployment is not fully ready.")
verify_pod("api", api_row)

# 2. Web: unchanged parent, deploy with the original guards.
web = deployment("shopixy-web")
web_row = receipt["components"]["web"]
web_artifact = inputs["components"]["web"]
if web["metadata"]["uid"] != web_row["deploymentUid"] or web["metadata"]["uid"] != baseline["shopixy-web"]["uid"]:
    raise RuntimeError("Web deployment UID changed.")
if seal(web["spec"]) != baseline["shopixy-web"]["specSha256"] or seal(web["spec"]) != web_row["beforeSpecSha256"]:
    raise RuntimeError("Web changed since parent capture.")
index = next(i for i, c in enumerate(web["spec"]["template"]["spec"]["containers"]) if c["name"] == "web")
if web["spec"]["template"]["spec"]["containers"][index]["image"] != web_row["oldImage"]:
    raise RuntimeError("Web no longer runs the captured parent image.")
if digest(root / web_artifact["archive"]) != web_artifact["archiveSha256"]:
    raise RuntimeError("Release archive SHA256 mismatch.")
command(["k3s", "ctr", "-n", "k8s.io", "images", "import", str(root / web_artifact["archive"])])
identity = json.loads(command(["k3s", "crictl", "inspecti", web_artifact["tag"]]))
if identity["status"]["id"] != web_artifact["configDigest"]:
    raise RuntimeError("Imported web image config identity mismatch.")
image = web_artifact["tag"].split(":")[0] + "@" + web_artifact["manifestDigest"]
command(["k3s", "ctr", "-n", "k8s.io", "images", "tag", web_artifact["tag"], image])
imported = json.loads(command(["k3s", "crictl", "inspecti", image]))
if imported["status"]["id"] != web_artifact["configDigest"]:
    raise RuntimeError("Digest-pinned web image config identity mismatch.")
listing = command(["k3s", "ctr", "-n", "k8s.io", "images", "ls"]).splitlines()
rows = [line.split() for line in listing[1:] if line.split() and line.split()[0] == image]
if not rows or rows[0][2] != web_artifact["manifestDigest"]:
    raise RuntimeError("Imported web manifest digest mismatch.")
patch = [
    {"op": "test", "path": "/metadata/resourceVersion", "value": web["metadata"]["resourceVersion"]},
    {"op": "test", "path": f"/spec/template/spec/containers/{index}/image", "value": web_row["oldImage"]},
    {"op": "replace", "path": f"/spec/template/spec/containers/{index}/image", "value": image},
]
kube("patch", "deployment", "shopixy-web", "--type=json", "-p", json.dumps(patch))
kube("rollout", "status", "deployment/shopixy-web", "--timeout=300s", timeout=330)
web_after = deployment("shopixy-web")
expected = copy.deepcopy(web["spec"])
expected["template"]["spec"]["containers"][index]["image"] = image
if web_after["metadata"]["uid"] != web_row["deploymentUid"] or seal(web_after["spec"]) != seal(expected):
    raise RuntimeError("Web deployment changed beyond the authorized image field.")
web_row["afterSpecSha256"] = seal(web_after["spec"])
web_row["readyReplicas"] = web_after["status"].get("readyReplicas", 0)
if web_row["readyReplicas"] != web_after["spec"]["replicas"]:
    raise RuntimeError("Web deployment is not fully ready.")
verify_pod("web", web_row)

# 3. Every other deployment must still match the committed predeploy baseline.
final = {d["metadata"]["name"]: d for d in json.loads(kube("get", "deployments", "-o", "json"))["items"]}
untouched = [name for name in baseline if name not in ("shopixy-api", "shopixy-web")]
for name in untouched:
    if final[name]["metadata"]["uid"] != baseline[name]["uid"] or seal(final[name]["spec"]) != baseline[name]["specSha256"]:
        raise RuntimeError(f"Unrelated deployment {name} changed.")
receipt["unchangedOtherDeployments"] = untouched
receipt["completedAtUtc"] = datetime.datetime.now(datetime.timezone.utc).isoformat()
receipt["status"] = "DEPLOYED_RUNTIME_VERIFIED_BROWSER_PENDING"
(root / "deployment-receipt.json").write_text(json.dumps(receipt, indent=2) + "\n")
print(json.dumps(receipt, indent=2))
