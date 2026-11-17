"""Export exact running image references, without reading deployment secrets."""
import hashlib
import json
import subprocess
from pathlib import Path

root = Path("/var/tmp/shopixy-finance-superadmin-20261117")
root.mkdir(mode=0o700, exist_ok=True)
expected = {
    "api": "docker.io/shopixy/api@sha256:cb900c183069f94311142ad9c5a16fa23266611d3c29f674bae64040189ac307",
    "web": "docker.io/shopixy/web@sha256:627af0b79e1a8bfa4be76691df0f43c938c9b586727d11c2f223e43580c21568",
}
receipt = {}
for component, image in expected.items():
    deployment = json.loads(subprocess.check_output(
        ["kubectl", "-n", "shopixy-app", "get", "deployment", f"shopixy-{component}", "-o", "json"]))
    container = next(c for c in deployment["spec"]["template"]["spec"]["containers"] if c["name"] == component)
    if container["image"] != image:
        raise RuntimeError("Production identity changed; review before exporting parents.")
    archive = root / f"{component}-parent.tar"
    subprocess.run(["k3s", "ctr", "-n", "k8s.io", "images", "export",
                    "--platform", "linux/amd64", str(archive), image], check=True, capture_output=True)
    digest = hashlib.sha256()
    with archive.open("rb") as stream:
        for block in iter(lambda: stream.read(1024 * 1024), b""):
            digest.update(block)
    receipt[component] = {"image": image, "archive": archive.name,
                          "archiveSha256": digest.hexdigest()}
(root / "parents.json").write_text(json.dumps(receipt, indent=2) + "\n")
print(json.dumps(receipt, indent=2))
