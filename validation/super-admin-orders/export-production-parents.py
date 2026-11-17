"""Export exact parent images; no application/DB mutation, credentials or manifests with env."""
import hashlib
import json
import subprocess
from pathlib import Path

root = Path("/var/tmp/shopixy-orders-superadmin-20261117")
root.mkdir(mode=0o700, exist_ok=True)
expected = {
    "api": "localhost/shopixy/api@sha256:5a3bab31b628f8a3dfebd88c9eb0d60083176f9fc5f0b8a4d7c43845844e67ba",
    "web": "localhost/shopixy/web@sha256:207a1794c0411b5a895a9cb411b4b91836c779b3283bfea127cc7e888eb216bf",
}
receipt = {}
for component, image in expected.items():
    deployment = json.loads(subprocess.check_output(
        ["kubectl", "-n", "shopixy-app", "get", "deployment", f"shopixy-{component}", "-o", "json"]))
    container = next(c for c in deployment["spec"]["template"]["spec"]["containers"] if c["name"] == component)
    if container["image"] != image:
        raise RuntimeError("Production identity changed; refusing parent export.")
    archive = root / f"{component}-parent.tar"
    if archive.exists():
        raise RuntimeError("Parent archive already exists; verify rather than overwrite.")
    subprocess.run(["k3s", "ctr", "-n", "k8s.io", "images", "export",
                    "--platform", "linux/amd64", str(archive), image], check=True, capture_output=True)
    digest = hashlib.sha256()
    with archive.open("rb") as stream:
        for block in iter(lambda: stream.read(1024 * 1024), b""):
            digest.update(block)
    receipt[component] = {"image": image, "archive": archive.name, "archiveSha256": digest.hexdigest()}
(root / "parents.json").write_text(json.dumps(receipt, indent=2) + "\n")
print(json.dumps(receipt, indent=2))
