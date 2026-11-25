"""Build patched Nginx filesystem in an isolated disposable container.

No Kubernetes objects, host package installs, bind mounts, listeners, credentials
or registry writes. Uses existing ctr in a dedicated Marketing build namespace.
"""
import argparse
import gzip
import hashlib
import io
import json
from pathlib import Path
import shlex
import subprocess
import tarfile
import re
import uuid

APP = Path(__file__).resolve().parents[1]


def prepare(args):
    out = Path(args.output).resolve()
    out.mkdir(parents=True, exist_ok=True)
    base = json.loads((APP / "container/base.json").read_text(encoding="utf-8"))
    lock = json.loads((APP / "container/runtime-lock.json").read_text(encoding="utf-8"))
    namespace = "shopyxi-marketing-build-20261125"
    image = "docker.io/" + base["repository"] + "@" + base["digest"]
    ssh = ["ssh", "-oBatchMode=yes", "-oStrictHostKeyChecking=yes", args.host]
    def remote(command):
        return subprocess.check_output(ssh + [shlex.join(command)])
    print(remote(["k3s", "ctr", "-n", namespace, "images", "pull", "--platform", "linux/amd64", image]).decode())
    # Host networking is outbound download only; this process never starts Nginx.
    # Package signatures are verified by standard apk using the base's Alpine keys.
    install = "apk add --no-cache " + shlex.join(lock["packages"])
    script = install + " >&2; apk info -v >&2; tar -czf /tmp/runtime.tar.gz --exclude=./proc --exclude=./sys --exclude=./dev --exclude=./tmp --exclude=./run --exclude=./etc/hosts --exclude=./etc/hostname --exclude=./etc/resolv.conf --exclude=./var/cache/apk --exclude=./var/log -C / .; sha256sum /tmp/runtime.tar.gz"
    container = "marketing-runtime-packaging-" + uuid.uuid4().hex[:12]
    # Persist the archive before process exit, then use SCP, not ctr's large
    # stdout forwarding (which can truncate when the task exits).
    try:
        result = subprocess.run(ssh + [shlex.join(["k3s", "ctr", "-n", namespace, "run",
            "--net-host", "--user", "0:0", "--memory-limit", "268435456",
            image, container, "/bin/sh", "-ec", script])], capture_output=True, check=True)
        (out / "runtime-package-install.log").write_bytes(result.stderr)
        expected = result.stdout.decode().split()[0]
        mounts = remote(["k3s", "ctr", "-n", namespace, "snapshots", "mounts", "/var/tmp/marketing-runtime-unmounted", container]).decode()
        (out / "runtime-snapshot-mount-command.txt").write_text(mounts, encoding="utf-8")
        upper = re.search(r"upperdir=([^,\s]+)", mounts)
        if not upper or not upper[1].startswith("/var/lib/rancher/k3s/agent/containerd/"):
            raise ValueError("Unexpected dedicated builder snapshot location")
        archive = out / "runtime-filesystem.tar.gz"
        subprocess.run(["scp", "-q", "-oBatchMode=yes", "-oStrictHostKeyChecking=yes",
            args.host + ":" + upper[1] + "/tmp/runtime.tar.gz", str(archive)], check=True)
        archive_bytes = archive.read_bytes()
        if hashlib.sha256(archive_bytes).hexdigest() != expected:
            raise ValueError("Runtime archive transfer hash mismatch")
    finally:
        # Delete only this invocation's own exited task/container/snapshot.
        subprocess.run(ssh + [shlex.join(["k3s", "ctr", "-n", namespace, "tasks", "delete", container])],
            capture_output=True)
        subprocess.run(ssh + [shlex.join(["k3s", "ctr", "-n", namespace, "containers", "delete", container])],
            capture_output=True)
    # Normalize filesystem metadata; omit container mounts/caches/logs.
    normalized = io.BytesIO()
    with tarfile.open(fileobj=io.BytesIO(archive_bytes), mode="r:gz") as source:
        with tarfile.open(fileobj=normalized, mode="w", format=tarfile.PAX_FORMAT) as target:
            for entry in sorted(source.getmembers(), key=lambda x: x.name):
                entry.name = entry.name.removeprefix("./").rstrip("/")
                if not entry.name:
                    continue
                if entry.name.startswith("/") or ".." in Path(entry.name).parts:
                    raise ValueError("Unsafe runtime archive path")
                entry.mtime, entry.uname, entry.gname, entry.pax_headers = 0, "", "", {}
                target.addfile(entry, source.extractfile(entry) if entry.isfile() else None)
    raw = normalized.getvalue()
    compressed = gzip.compress(raw, mtime=0)
    layer_sha, diff_sha = hashlib.sha256(compressed).hexdigest(), hashlib.sha256(raw).hexdigest()
    (out / "runtime-layer.tar.gz").write_bytes(compressed)
    receipt = {"base": base, "packages": lock["packages"], "layerSha256": layer_sha,
        "diffSha256": diff_sha, "apkSignatureVerification": True,
        "method": "Disposable container only; standard signed apk; no host OS or Kubernetes object changes"}
    (out / "runtime-receipt.json").write_text(json.dumps(receipt, indent=2) + "\n", encoding="utf-8")
    print(json.dumps(receipt, indent=2))


if __name__ == "__main__":
    parser = argparse.ArgumentParser()
    parser.add_argument("--host", default="shopixy-vps")
    parser.add_argument("--output", required=True)
    prepare(parser.parse_args())
