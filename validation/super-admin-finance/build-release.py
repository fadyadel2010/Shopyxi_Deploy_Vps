"""Local WSL/Podman only. Build narrow overlays from verified production parents."""
import hashlib
import json
import shutil
import subprocess
import tarfile
from pathlib import Path

source = Path.cwd()
root = Path("/mnt/c/Users/fadya/AppData/Local/Temp/opencode/finance-release-20261117")

def run(*args):
    return subprocess.check_output(args, text=True).strip()

def digest(path):
    h = hashlib.sha256()
    with path.open("rb") as stream:
        for block in iter(lambda: stream.read(1024 * 1024), b""):
            h.update(block)
    return h.hexdigest()

def image_manifest(archive):
    with tarfile.open(archive) as tar:
        def blob(descriptor):
            return json.load(tar.extractfile("blobs/" + descriptor["digest"].replace(":", "/")))
        manifest = json.load(tar.extractfile("index.json"))["manifests"][0]
        body = blob(manifest)
        while "manifests" in body:
            manifest = next(m for m in body["manifests"] if m.get("platform", {}).get("architecture") == "amd64")
            body = blob(manifest)
        return manifest["digest"], body["config"]["digest"]

dirty = run("git", "status", "--porcelain", "--untracked-files=no")
if dirty:
    raise RuntimeError("Commit validated tracked source before release build.")
revision = run("git", "rev-parse", "HEAD")
parents = json.loads((source / "validation/super-admin-finance/production-parents.json").read_text())
receipt = {"sourceCommit": revision, "delivery": "SSH_OCI_ARCHIVE_TO_K3S_CONTAINERD", "components": {}}
for component in ("api", "web"):
    parent_archive = root / f"{component}-parent.tar"
    if digest(parent_archive) != parents[component]["archiveSha256"]:
        raise RuntimeError("Production parent archive hash mismatch.")
    parent_manifest, parent_config = image_manifest(parent_archive)
    if parents[component]["image"].split("@")[1] != parent_manifest:
        # containerd may export an index whose reference digest differs from its
        # platform manifest. Both are retained in the evidence; config must match.
        print(f"{component}: retaining exported platform manifest {parent_manifest}", flush=True)
    subprocess.run(["podman", "load", "--input", str(parent_archive)], check=True)
    local_parent = f"localhost/shopixy/{component}:finance-parent-20261117"
    subprocess.run(["podman", "tag", parent_config, local_parent], check=True)
    context = root / f"{component}-context"
    context.mkdir(exist_ok=True)
    artifact_hashes = {}
    if component == "api":
        for name in ("PointOfSale.API.dll", "PointOfSale.Core.dll", "PointOfSale.Infrastructure.dll"):
            artifact = source / "PointOfSale.API/bin/Release/net10.0" / name
            shutil.copy2(artifact, context / name)
            artifact_hashes[name] = digest(artifact)
        instructions = "COPY --chown=1654:1654 PointOfSale.API.dll PointOfSale.Core.dll PointOfSale.Infrastructure.dll /app/\nUSER 1654:1654\n"
    else:
        dist = source / "PointOfSale.Web/dist"
        shutil.copytree(dist, context / "dist", dirs_exist_ok=True)
        artifact_hashes = {str(p.relative_to(dist)).replace("\\", "/"): digest(p) for p in sorted(dist.rglob("*")) if p.is_file()}
        instructions = "USER 0\nRUN find /usr/share/nginx/html -mindepth 1 -delete\nCOPY --chown=101:101 dist/ /usr/share/nginx/html/\nUSER 101:101\n"
    input_hash = hashlib.sha256(json.dumps(artifact_hashes, sort_keys=True, separators=(",", ":")).encode()).hexdigest()
    (context / "Dockerfile").write_text(
        f'FROM {local_parent}\nLABEL org.opencontainers.image.revision="{revision}" '
        f'shopixy.io/source-input-sha256="{input_hash}" shopixy.io/release="finance-superadmin-20261117"\n' + instructions)
    tag = f"localhost/shopixy/{component}:finance-{revision[:12]}"
    subprocess.run(["podman", "build", "--pull=never", "--network=none", "--format=oci",
                    "--tag", tag, str(context)], check=True)
    archive = root / f"{component}.tar"
    subprocess.run(["podman", "save", "--format", "oci-archive", "--output", str(archive), tag], check=True)
    manifest, config = image_manifest(archive)
    receipt["components"][component] = {
        "tag": tag, "manifestDigest": manifest, "configDigest": config,
        "archive": archive.name, "archiveSha256": digest(archive), "sourceInputSha256": input_hash,
        "parentImage": parents[component]["image"], "parentPlatformManifest": parent_manifest,
        "parentConfigDigest": parent_config, "artifactSha256": artifact_hashes,
    }
    (root / "release-inputs.json").write_text(json.dumps(receipt, indent=2) + "\n")
print(json.dumps({k: {c: v for c, v in d.items() if c != "artifactSha256"} for k, d in receipt["components"].items()}, indent=2))
