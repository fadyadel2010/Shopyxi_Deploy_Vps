"""Existing daemonless OCI workflow; no daemon, registry push or VPS modification.

Consumes only a guarded public artifact from a clean committed Marketing tree.
All base blobs and artifact files are hash checked. Outputs stay outside source.
"""
import argparse
import copy
import gzip
import hashlib
import io
import json
from pathlib import Path
import re
import subprocess
import tarfile

APP = Path(__file__).resolve().parents[1]
MEDIA = "application/vnd.oci.image.manifest.v1+json"


def sha(raw):
    return hashlib.sha256(raw).hexdigest()


def canonical(value):
    return json.dumps(value, separators=(",", ":"), ensure_ascii=False).encode()


def build(args):
    commit = args.source_commit
    if not re.fullmatch(r"[a-f0-9]{40}", commit):
        raise ValueError("Full source commit required")
    actual = subprocess.check_output(["git", "rev-parse", "HEAD"], cwd=APP, text=True).strip()
    dirty = subprocess.check_output(["git", "status", "--porcelain", "--", "."], cwd=APP, text=True).strip()
    if actual != commit or dirty:
        raise ValueError("Marketing must be clean and match the named commit")
    # Re-run the actual public guard/build; never consume a review artifact.
    subprocess.run(["node", "scripts/build.mjs", "--public"], cwd=APP, check=True, stdout=subprocess.DEVNULL)
    receipt_raw = (APP / "dist/build-receipt.json").read_bytes()
    receipt = json.loads(receipt_raw)
    if receipt["mode"] != "public-lean" or receipt["publicReleaseBlocked"]:
        raise ValueError("Guarded public artifact required")
    out = Path(args.output).resolve()
    if out == APP or APP in out.parents:
        raise ValueError("Output must be outside Marketing source")
    out.mkdir(parents=True, exist_ok=True)
    blobs = out / "blobs/sha256"
    blobs.mkdir(parents=True, exist_ok=True)
    base = json.loads((APP / "container/base.json").read_text(encoding="utf-8"))
    def descriptor(raw, media):
        digest = sha(raw)
        (blobs / digest).write_bytes(raw)
        return {"mediaType": media, "digest": "sha256:" + digest, "size": len(raw)}

    runtime_lock = json.loads((APP / "container/runtime-lock.json").read_text(encoding="utf-8"))
    runtime = (out / "runtime-layer.tar.gz").read_bytes()
    runtime_raw = gzip.decompress(runtime)
    if sha(runtime) != runtime_lock["layerSha256"] or sha(runtime_raw) != runtime_lock["diffSha256"]:
        raise ValueError("Runtime filesystem differs from reviewed signed-package lock")
    runtime_receipt = json.loads((out / "runtime-receipt.json").read_text(encoding="utf-8"))
    if runtime_receipt["base"] != base or runtime_receipt["packages"] != runtime_lock["packages"] or not runtime_receipt["apkSignatureVerification"]:
        raise ValueError("Runtime provenance mismatch")
    manifest = {"schemaVersion": 2, "layers": [descriptor(runtime, "application/vnd.oci.image.layer.v1.tar+gzip")]}
    config = {"architecture": "amd64", "os": "linux",
        "rootfs": {"type": "layers", "diff_ids": ["sha256:" + sha(runtime_raw)]},
        "history": [{"created_by": "Pinned Alpine and signature-verified patched stable Nginx runtime"}]}
    files = {}
    stream = io.BytesIO()
    with tarfile.open(fileobj=stream, mode="w", format=tarfile.USTAR_FORMAT) as layer:
        entries = []
        for item in receipt["sizes"]:
            relative = item["file"]
            if relative in ["routes.json", "publication-gates.json"]:
                continue
            if ".." in Path(relative).parts or Path(relative).is_absolute():
                raise ValueError("Unsafe artifact path")
            raw = (APP / "dist" / relative).read_bytes()
            if sha(raw) != item["sha256"]:
                raise ValueError("Artifact hash mismatch: " + relative)
            entries.append(("site/" + relative, raw))
        entries.append(("etc/nginx/nginx.conf", (APP / "nginx.conf").read_bytes()))
        for name, raw in sorted(entries):
            info = tarfile.TarInfo(name)
            info.size, info.mode, info.uid, info.gid, info.mtime = len(raw), 0o644, 101, 101, 0
            layer.addfile(info, io.BytesIO(raw))
            files[name] = sha(raw)
    raw_layer = stream.getvalue()
    config["config"] = {
        "User": "101:101", "WorkingDir": "/",
        "Entrypoint": ["/usr/sbin/nginx", "-g", "daemon off;"],
        "Env": ["PATH=/usr/local/sbin:/usr/local/bin:/usr/sbin:/usr/bin:/sbin:/bin"],
        "ExposedPorts": {"8080/tcp": {}, "8081/tcp": {}}, "StopSignal": "SIGQUIT",
        "Healthcheck": {"Test": ["CMD-SHELL", "wget -q -O /dev/null http://127.0.0.1:8081/healthz || exit 1"],
            "Interval": 30000000000, "Timeout": 3000000000, "StartPeriod": 5000000000, "Retries": 3},
        "Labels": {"org.opencontainers.image.title": "SHOPYXI Marketing",
            "org.opencontainers.image.revision": commit,
            "org.opencontainers.image.base.digest": base["digest"],
            "shopyxi.build.receipt.sha256": sha(receipt_raw)}
    }
    config["rootfs"]["diff_ids"].append("sha256:" + sha(raw_layer))
    config.setdefault("history", []).append({"created_by": "COPY guarded public site and Marketing nginx.conf"})
    final = copy.deepcopy(manifest)
    final["mediaType"] = MEDIA
    final["config"] = descriptor(canonical(config), "application/vnd.oci.image.config.v1+json")
    final["layers"].append(descriptor(gzip.compress(raw_layer, mtime=0), "application/vnd.oci.image.layer.v1.tar+gzip"))
    desc = descriptor(canonical(final), MEDIA)
    tag = "localhost/shopyxi/marketing:" + commit
    desc["annotations"] = {"org.opencontainers.image.ref.name": tag}
    archive = out / ("marketing-" + commit + ".oci.tar")
    if archive.exists():
        raise ValueError("Never overwrite an existing release archive")
    payloads = {"oci-layout": b'{"imageLayoutVersion":"1.0.0"}',
        "index.json": canonical({"schemaVersion": 2, "manifests": [desc]})}
    for digest in sorted({final["config"]["digest"], desc["digest"], *[x["digest"] for x in final["layers"]]}):
        payloads["blobs/" + digest.replace(":", "/")] = (blobs / digest.split(":")[1]).read_bytes()
    with tarfile.open(archive, mode="w", format=tarfile.USTAR_FORMAT) as tar:
        for name, raw in sorted(payloads.items()):
            info = tarfile.TarInfo(name)
            info.size, info.mode, info.mtime = len(raw), 0o644, 0
            tar.addfile(info, io.BytesIO(raw))
    provenance = {"sourceCommit": commit, "tag": tag,
        "image": "localhost/shopyxi/marketing@" + desc["digest"], "digest": desc["digest"],
        "base": base, "runtimeLock": runtime_lock, "archive": archive.name, "archiveSha256": sha(archive.read_bytes()),
        "publicReceiptSha256": sha(receipt_raw), "files": files,
        "workflow": "Verified public artifact -> pinned base OCI layers -> deterministic copy layer -> node containerd import",
        "runtimeUser": "101:101"}
    (out / "image-provenance.json").write_text(json.dumps(provenance, indent=2) + "\n", encoding="utf-8")
    print(json.dumps({k: v for k, v in provenance.items() if k != "files"}, indent=2))


if __name__ == "__main__":
    parser = argparse.ArgumentParser()
    parser.add_argument("--source-commit", required=True)
    parser.add_argument("--output", required=True)
    build(parser.parse_args())
