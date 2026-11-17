"""Explicit rollback only: image-field compare-and-swap, no data/schema changes."""
import json
import subprocess
from pathlib import Path

root = Path.cwd()
receipt = json.loads((root / "deployment-receipt.json").read_text())

def kube(*args):
    return subprocess.check_output(
        ["kubectl", "--request-timeout=30s", "-n", "shopixy-app", *args], text=True, timeout=330)

for component, row in reversed(list(receipt["components"].items())):
    if component not in ("api", "web"):
        raise RuntimeError("Unexpected rollback component.")
    name = f"shopixy-{component}"
    deployment = json.loads(kube("get", "deployment", name, "-o", "json"))
    index = next(i for i,c in enumerate(deployment["spec"]["template"]["spec"]["containers"]) if c["name"] == component)
    current = deployment["spec"]["template"]["spec"]["containers"][index]["image"]
    if current == row["oldImage"]:
        continue
    if current != row["newImage"] or deployment["metadata"]["uid"] != row["deploymentUid"]:
        raise RuntimeError("Rollback authority changed; refusing to overwrite concurrent deployment.")
    patch = [
        {"op":"test","path":"/metadata/resourceVersion","value":deployment["metadata"]["resourceVersion"]},
        {"op":"test","path":f"/spec/template/spec/containers/{index}/image","value":row["newImage"]},
        {"op":"replace","path":f"/spec/template/spec/containers/{index}/image","value":row["oldImage"]},
    ]
    kube("patch","deployment",name,"--type=json","-p",json.dumps(patch))
    kube("rollout","status",f"deployment/{name}","--timeout=300s")
print("Original API/Web images restored; no database/schema action executed.")
