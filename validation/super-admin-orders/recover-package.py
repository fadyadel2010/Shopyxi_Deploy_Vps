"""Recover the interrupted owned package using compressed local OCI transport.

No registry/network delivery, source rebuild, parent replacement or guard bypass.
The already-built API context/image must match every sealed artifact and label.
"""
import hashlib
import json
import shutil
import subprocess
import tarfile
from pathlib import Path

source = Path.cwd()
root = Path("/mnt/c/Users/fadya/AppData/Local/Temp/opencode/orders-release-20261117")
git = "/mnt/c/Program Files/Git/cmd/git.exe"

def run(*args):
    return subprocess.check_output(args, text=True).strip()

def digest(path):
    h = hashlib.sha256()
    with path.open("rb") as stream:
        for block in iter(lambda: stream.read(1024 * 1024), b""):
            h.update(block)
    return h.hexdigest()

def identity(archive):
    with tarfile.open(archive) as tar:
        def blob(d):
            return json.load(tar.extractfile("blobs/" + d["digest"].replace(":", "/")))
        d = json.load(tar.extractfile("index.json"))["manifests"][0]
        m = blob(d)
        while "manifests" in m:
            d = next(x for x in m["manifests"] if x.get("platform", {}).get("architecture") == "amd64")
            m = blob(d)
        return d["digest"], m["config"]["digest"]

if run(git, "status", "--porcelain", "--untracked-files=all"):
    raise RuntimeError("Application checkout must remain clean.")
revision = run(git, "rev-parse", "HEAD")
gates_path = source / "validation/super-admin-orders/test-gates.json"
gates = json.loads(gates_path.read_text())
builds = gates["builds"]
if not gates["pass"] or not builds["pass"] or builds["sourceCommit"] != revision:
    raise RuntimeError("Passing committed-source acceptance is required.")
for name, expected in builds["sourceFilesSha256"].items():
    if digest(source / name) != expected:
        raise RuntimeError("Sealed source changed: " + name)
for name, expected in builds["apiArtifactSha256"].items():
    if digest(source / "PointOfSale.API/bin/Release/net10.0" / name) != expected:
        raise RuntimeError("Sealed API artifact changed: " + name)
dist = source / "PointOfSale.Web/dist"
web_hashes = {p.relative_to(dist).as_posix(): digest(p) for p in sorted(dist.rglob("*")) if p.is_file()}
if web_hashes != builds["webArtifactSha256"]:
    raise RuntimeError("Sealed Web artifacts changed.")
parents = json.loads((source / "validation/super-admin-orders/production-parents.json").read_text())
receipt = {"sourceCommit": revision, "delivery": "SSH_OCI_ARCHIVE_TO_K3S_CONTAINERD",
           "qualityGatesSha256": digest(gates_path), "archiveCompression": "gzip", "components": {}}
for component in ("api", "web"):
    archive = root / f"{component}.tar"
    if archive.exists():
        raise RuntimeError("Do not overwrite an existing release archive.")
    parent = root / f"{component}-parent.tar"
    if digest(parent) != parents[component]["archiveSha256"]:
        raise RuntimeError("Parent archive mismatch.")
    parent_manifest, parent_config = identity(parent)
    subprocess.run(["podman", "load", "--input", str(parent)], check=True)
    parent_tag = f"localhost/shopixy/{component}:orders-parent-20261117"
    subprocess.run(["podman", "tag", parent_config, parent_tag], check=True)
    context = root / f"{component}-context"
    if component == "api":
        artifacts = builds["apiArtifactSha256"]
        expected_files = set(artifacts) | {"Dockerfile"}
        if not context.is_dir() or {p.name for p in context.iterdir()} != expected_files:
            raise RuntimeError("Interrupted API context is not the exact owned context.")
        for name, expected in artifacts.items():
            if digest(context / name) != expected:
                raise RuntimeError("Interrupted API context changed.")
        instructions = "COPY --chown=1654:1654 PointOfSale.API.dll PointOfSale.Core.dll PointOfSale.Infrastructure.dll /app/\nUSER 1654:1654\n"
    else:
        if context.exists():
            raise RuntimeError("Unexpected existing Web context.")
        context.mkdir()
        shutil.copytree(dist, context / "dist")
        artifacts = web_hashes
        instructions = "USER 0\nRUN find /usr/share/nginx/html -mindepth 1 -delete\nCOPY --chown=101:101 dist/ /usr/share/nginx/html/\nUSER 101:101\n"
    input_hash = hashlib.sha256(json.dumps(artifacts, sort_keys=True, separators=(",", ":")).encode()).hexdigest()
    dockerfile = (f'FROM {parent_tag}\nLABEL org.opencontainers.image.revision="{revision}" '
                  f'shopixy.io/source-input-sha256="{input_hash}" shopixy.io/release="orders-superadmin-20261117"\n' + instructions)
    tag = f"localhost/shopixy/{component}:orders-{revision[:12]}"
    if component == "api":
        if (context / "Dockerfile").read_text() != dockerfile:
            raise RuntimeError("Interrupted API build instructions changed.")
        image = json.loads(run("podman", "image", "inspect", tag))[0]
        labels = image["Config"]["Labels"]
        if labels.get("org.opencontainers.image.revision") != revision or labels.get("shopixy.io/source-input-sha256") != input_hash:
            raise RuntimeError("Interrupted API image provenance mismatch.")
    else:
        (context / "Dockerfile").write_text(dockerfile)
        subprocess.run(["podman", "build", "--pull=never", "--network=none", "--format=oci", "--tag", tag, str(context)], check=True)
    subprocess.run(["podman", "push", "--format=oci", "--compression-format=gzip", "--force-compression",
                    tag, f"oci-archive:{archive}:{tag}"], check=True)
    manifest, config = identity(archive)
    image_config = run("podman", "image", "inspect", "--format", "{{.Id}}", tag)
    if config.removeprefix("sha256:") != image_config.removeprefix("sha256:"):
        raise RuntimeError("Compressed archive changed the built image config.")
    receipt["components"][component] = {
        "tag": tag, "manifestDigest": manifest, "configDigest": config, "archive": archive.name,
        "archiveSha256": digest(archive), "sourceInputSha256": input_hash,
        "parentImage": parents[component]["image"], "parentPlatformManifest": parent_manifest,
        "parentConfigDigest": parent_config, "artifactSha256": artifacts,
    }
    (root / "release-inputs.json").write_text(json.dumps(receipt, indent=2) + "\n")
print(json.dumps({k: {n: v for n, v in row.items() if n != "artifactSha256"}
                  for k, row in receipt["components"].items()}, indent=2))
