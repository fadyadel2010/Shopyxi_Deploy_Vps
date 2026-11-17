"""Guarded API/Web-only K3s deployment. Run from the private sealed release folder."""
import copy
import datetime
import hashlib
import json
import subprocess
from pathlib import Path

root = Path.cwd()
inputs = json.loads((root / "release-inputs.json").read_text())

def command(args, input_text=None, timeout=180):
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

if inputs.get("delivery") != "SSH_OCI_ARCHIVE_TO_K3S_CONTAINERD":
    raise RuntimeError("Unsupported delivery contract.")
before = {d["metadata"]["name"]: d for d in json.loads(kube("get", "deployments", "-o", "json"))["items"]}
receipt = {"startedAtUtc": datetime.datetime.now(datetime.timezone.utc).isoformat(),
           "sourceCommit": inputs["sourceCommit"], "migrationsExecuted": False,
           "businessDataWritesExecuted": False, "components": {}, "status": "PREPARED"}
for component, artifact in inputs["components"].items():
    if component not in ("api", "web"):
        raise RuntimeError("Unapproved component.")
    deployment = before[f"shopixy-{component}"]
    container = next(c for c in deployment["spec"]["template"]["spec"]["containers"] if c["name"] == component)
    if container["image"] != artifact["parentImage"]:
        raise RuntimeError("Production changed since parent capture.")
    if digest(root / artifact["archive"]) != artifact["archiveSha256"]:
        raise RuntimeError("Release archive SHA256 mismatch.")
    command(["k3s", "ctr", "-n", "k8s.io", "images", "import", str(root / artifact["archive"])])
    identity = json.loads(command(["k3s", "crictl", "inspecti", artifact["tag"]]))
    if identity["status"]["id"] != artifact["configDigest"]:
        raise RuntimeError("Imported image config identity mismatch.")
    image = artifact["tag"].split(":")[0] + "@" + artifact["manifestDigest"]
    command(["k3s", "ctr", "-n", "k8s.io", "images", "tag", artifact["tag"], image])
    imported = json.loads(command(["k3s", "crictl", "inspecti", image]))
    if imported["status"]["id"] != artifact["configDigest"]:
        raise RuntimeError("Digest-pinned image config identity mismatch.")
    listing = command(["k3s", "ctr", "-n", "k8s.io", "images", "ls"]).splitlines()
    rows = [line.split() for line in listing[1:] if line.split() and line.split()[0] == image]
    if not rows or rows[0][2] != artifact["manifestDigest"]:
        raise RuntimeError("Imported manifest digest mismatch; refusing to patch.")
    receipt["components"][component] = {
        "oldImage": container["image"], "newImage": image,
        "manifestDigest": artifact["manifestDigest"],
        "configDigest": artifact["configDigest"], "sourceInputSha256": artifact["sourceInputSha256"],
        "deploymentUid": deployment["metadata"]["uid"],
        "beforeSpecSha256": seal(deployment["spec"]),
    }

(root / "deployment-receipt.json").write_text(json.dumps(receipt, indent=2) + "\n")
for component, row in receipt["components"].items():
    name = f"shopixy-{component}"
    deployment = before[name]
    index = next(i for i, c in enumerate(deployment["spec"]["template"]["spec"]["containers"]) if c["name"] == component)
    patch = [
        {"op": "test", "path": "/metadata/resourceVersion", "value": deployment["metadata"]["resourceVersion"]},
        {"op": "test", "path": f"/spec/template/spec/containers/{index}/image", "value": row["oldImage"]},
        {"op": "replace", "path": f"/spec/template/spec/containers/{index}/image", "value": row["newImage"]},
    ]
    kube("patch", "deployment", name, "--type=json", "-p", json.dumps(patch))
    kube("rollout", "status", f"deployment/{name}", "--timeout=300s", timeout=330)
    after = json.loads(kube("get", "deployment", name, "-o", "json"))
    expected = copy.deepcopy(deployment["spec"])
    expected["template"]["spec"]["containers"][index]["image"] = row["newImage"]
    if after["metadata"]["uid"] != row["deploymentUid"] or seal(after["spec"]) != seal(expected):
        raise RuntimeError("Deployment changed beyond the authorized image field.")
    row["afterSpecSha256"] = seal(after["spec"])
    row["readyReplicas"] = after["status"].get("readyReplicas", 0)
    if row["readyReplicas"] != after["spec"]["replicas"]:
        raise RuntimeError("Deployment is not fully ready.")
    pods = json.loads(kube("get", "pods", "-l", f"app=shopixy-{component}", "-o", "json"))["items"]
    current = [p for p in pods if not p["metadata"].get("deletionTimestamp") and
               any(c["image"] == row["newImage"] for c in p["spec"]["containers"])]
    if not current:
        # Label conventions are not assumed when selecting the new ready pod.
        all_pods = json.loads(kube("get", "pods", "-o", "json"))["items"]
        current = [p for p in all_pods if not p["metadata"].get("deletionTimestamp") and
                   any(c["name"] == component and c["image"] == row["newImage"] for c in p["spec"]["containers"])]
    pod = next(p for p in current if all(c.get("ready") for c in p["status"].get("containerStatuses", [])))
    row["pod"] = pod["metadata"]["name"]
    container_status = next(c for c in pod["status"]["containerStatuses"] if c["name"] == component)
    if container_status["imageID"].split("@")[-1] not in (row["manifestDigest"], row["configDigest"]):
        raise RuntimeError("Running pod image identity mismatch.")
    row["runningImageId"] = container_status["imageID"]
    if component == "api":
        paths = {name: "/app/" + name for name in inputs["components"][component]["artifactSha256"]}
    else:
        selected = [name for name in inputs["components"][component]["artifactSha256"]
                    if name == "index.html" or (name.startswith("assets/index-") and name.endswith(".js"))]
        paths = {name: "/usr/share/nginx/html/" + name for name in selected}
    runtime = kube("exec", row["pod"], "-c", component, "--", "sha256sum", *paths.values())
    hashes = {line.split(None, 1)[1].strip(): line.split(None, 1)[0] for line in runtime.splitlines()}
    for name, path in paths.items():
        if hashes.get(path) != inputs["components"][component]["artifactSha256"][name]:
            raise RuntimeError("Running application artifact differs from validated build.")
    row["runtimeArtifactHashesMatched"] = True
    row["verifiedRuntimeFiles"] = list(paths)
    (root / "deployment-receipt.json").write_text(json.dumps(receipt, indent=2) + "\n")

after_all = {d["metadata"]["name"]: d for d in json.loads(kube("get", "deployments", "-o", "json"))["items"]}
untouched = [name for name in before if name not in ("shopixy-api", "shopixy-web")]
if set(before) != set(after_all) or any(seal(before[name]["spec"]) != seal(after_all[name]["spec"]) for name in untouched):
    raise RuntimeError("An unrelated deployment changed; investigate concurrent work.")
receipt["unchangedOtherDeployments"] = untouched
receipt["completedAtUtc"] = datetime.datetime.now(datetime.timezone.utc).isoformat()
receipt["status"] = "DEPLOYED_RUNTIME_VERIFIED_BROWSER_PENDING"
(root / "deployment-receipt.json").write_text(json.dumps(receipt, indent=2) + "\n")
print(json.dumps(receipt, indent=2))
