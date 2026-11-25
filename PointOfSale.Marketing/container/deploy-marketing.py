"""Marketing-only image rollout with compare-and-swap and automatic rollback.

Run only after reviewed source is pushed, image scan passes and OCI provenance
matches. Uses the existing SSH/OCI/containerd delivery path, no registry.
"""
import argparse
import hashlib
import json
from pathlib import Path
import shlex
import subprocess
import tarfile
import urllib.request
import re

APP = Path(__file__).resolve().parents[1]
NS, NAME = "shopixy-app", "shopyxi-marketing"


def deploy(args):
    out = Path(args.evidence).resolve()
    provenance = json.loads((out / "image-provenance.json").read_text(encoding="utf-8"))
    commit, image = provenance["sourceCommit"], provenance["image"]
    ssh = ["ssh", "-oBatchMode=yes", "-oStrictHostKeyChecking=yes", args.host]

    def remote(*command):
        return subprocess.check_output(ssh + [shlex.join(command)], text=True)

    def kube(*command):
        return remote("kubectl", "-n", NS, *command)

    def save(name, value):
        (out / name).write_text(json.dumps(value, indent=2) + "\n", encoding="utf-8")

    def snapshot():
        return json.loads(kube("get", "deployment", NAME, "-o", "json"))

    if subprocess.check_output(["git", "rev-parse", "HEAD"], cwd=APP, text=True).strip() != commit:
        raise ValueError("Image/source commit mismatch")
    if subprocess.check_output(["git", "status", "--porcelain", "--", "."], cwd=APP, text=True).strip():
        raise ValueError("Marketing tree is not clean")
    default = subprocess.check_output(["git", "symbolic-ref", "refs/remotes/origin/HEAD"], cwd=APP, text=True).strip().split("/")[-1]
    remote_sha = subprocess.check_output(["git", "ls-remote", "origin", "refs/heads/" + default], cwd=APP, text=True).split()[0]
    if remote_sha != commit:
        raise ValueError("Default branch does not match image source")
    scan = json.loads((out / "image-vulnerabilities.json").read_text(encoding="utf-8"))
    if "Results" not in scan or any(v["Severity"] in ["HIGH", "CRITICAL"] for r in scan["Results"] for v in r.get("Vulnerabilities", [])):
        raise ValueError("Completed image scan with no HIGH/CRITICAL vulnerabilities required")
    with tarfile.open(out / provenance["archive"]) as archive:
        index = json.load(archive.extractfile("index.json"))
        digest = index["manifests"][0]["digest"]
        manifest = json.load(archive.extractfile("blobs/" + digest.replace(":", "/")))
        if digest != provenance["digest"] or scan["Metadata"]["ImageID"] != manifest["config"]["digest"]:
            raise ValueError("Vulnerability report is not for this exact image")
    nodes = json.loads(remote("kubectl", "get", "nodes", "-o", "json"))["items"]
    if len(nodes) != 1 or nodes[0]["metadata"]["name"] != "shopixy":
        raise ValueError("Node inventory changed; verify image delivery to every required node before proceeding")
    previous = snapshot()
    save("deployment-immediately-before.json", previous)
    previous_image = previous["spec"]["template"]["spec"]["containers"][0]["image"]
    if "@sha256:" not in previous_image or previous["spec"]["template"]["spec"]["containers"][0]["name"] != "marketing":
        raise ValueError("Unexpected deployment identity or nonimmutable rollback image")
    if previous.get("status", {}).get("readyReplicas") != previous["spec"]["replicas"]:
        raise ValueError("Previous Marketing deployment is not ready")
    save("rollback.json", {"image": previous_image, "uid": previous["metadata"]["uid"],
        "revision": previous["metadata"]["annotations"]["deployment.kubernetes.io/revision"]})
    # Retain actual previous public bytes, so rollback is verified rather than
    # declaring recovery from Kubernetes readiness alone.
    def public_response(path):
        request = urllib.request.Request("https://shopyxi.com" + path,
            headers={"User-Agent": "SHOPYXI-Marketing-Release-Verification/1.0"})
        return urllib.request.urlopen(request, timeout=30)

    def public_hash(path):
        with public_response(path) as response:
            return {"status": response.status, "sha256": hashlib.sha256(response.read()).hexdigest()}
    previous_public = {}
    route_map = json.loads((APP / "dist/routes.json").read_text(encoding="utf-8"))
    for pathname in [r["path"] for r in route_map] + ["/robots.txt", "/sitemap.xml"]:
        with public_response(pathname) as response:
            body = response.read()
            previous_public[pathname] = {"status": response.status, "sha256": hashlib.sha256(body).hexdigest()}
            if pathname == "/en/":
                for asset in re.findall(rb'(?:src|href)="(/assets/[^"]+)"', body):
                    asset_path = asset.decode("utf-8")
                    previous_public[asset_path] = public_hash(asset_path)
    save("public-before-rollout.json", previous_public)
    archive = out / provenance["archive"]
    if hashlib.sha256(archive.read_bytes()).hexdigest() != provenance["archiveSha256"]:
        raise ValueError("OCI archive hash mismatch")
    directory = "/var/tmp/shopyxi-marketing-" + commit
    remote("mkdir", "-p", directory)
    subprocess.run(["scp", "-q", "-oBatchMode=yes", "-oStrictHostKeyChecking=yes",
        str(archive), args.host + ":" + directory + "/" + archive.name], check=True)
    transferred = remote("sha256sum", directory + "/" + archive.name).split()[0]
    if transferred != provenance["archiveSha256"]:
        raise ValueError("Remote archive hash mismatch")
    print(remote("k3s", "ctr", "-n", "k8s.io", "images", "import", directory + "/" + archive.name))
    print(remote("k3s", "ctr", "-n", "k8s.io", "images", "tag", provenance["tag"], image))
    inspection = json.loads(remote("k3s", "crictl", "inspecti", image))
    if image not in inspection["status"].get("repoDigests", []):
        raise ValueError("CRI immutable digest mismatch")
    save("image-node-inspection.json", inspection)
    # Isolated network namespace, no public host port or Kubernetes object.
    dropped_caps = ["CHOWN", "DAC_OVERRIDE", "FSETID", "FOWNER", "MKNOD", "NET_RAW",
        "SETGID", "SETUID", "SETFCAP", "SETPCAP", "NET_BIND_SERVICE", "SYS_CHROOT", "KILL", "AUDIT_WRITE"]
    capability_args = [arg for cap in dropped_caps for arg in ["--cap-drop", "CAP_" + cap]]
    preflight = remote("k3s", "ctr", "-n", "k8s.io", "run", "--rm", "--read-only",
        "--user", "101:101", *capability_args, "--seccomp",
        "--memory-limit", "67108864",
        "--mount", "type=tmpfs,src=tmpfs,dst=/tmp,options=mode=1777",
        image, "marketing-preflight-" + commit[:12], "/bin/sh", "-ec",
        "nginx -e /dev/stderr -t; nginx -e /dev/stderr; wget -q -O - http://127.0.0.1:8081/healthz; "
        "wget -q --header='Host: shopyxi.com' -O /dev/null http://127.0.0.1:8080/ar/; "
        "find /site -type f -exec sha256sum {} \\;; sha256sum /etc/nginx/nginx.conf; nginx -e /dev/stderr -s quit")
    (out / "image-preflight.txt").write_text(preflight, encoding="utf-8")
    actual_files = {}
    for line in preflight.splitlines():
        parts = line.split()
        if len(parts) == 2 and parts[1].startswith(("/site/", "/etc/nginx/")):
            actual_files[parts[1].lstrip("/")] = parts[0]
    if actual_files != provenance["files"]:
        raise ValueError("Runtime image file inventory differs from public artifact provenance")

    def set_image(expected, target):
        patch = [{"op": "test", "path": "/metadata/uid", "value": expected["metadata"]["uid"]},
            {"op": "test", "path": "/metadata/resourceVersion", "value": expected["metadata"]["resourceVersion"]},
            {"op": "test", "path": "/spec/template/spec/containers/0/name", "value": "marketing"},
            {"op": "test", "path": "/spec/template/spec/containers/0/image", "value": expected["spec"]["template"]["spec"]["containers"][0]["image"]},
            {"op": "replace", "path": "/spec/template/spec/containers/0/image", "value": target}]
        return kube("patch", "deployment", NAME, "--type=json", "-p", json.dumps(patch))

    changed = False
    try:
        print(set_image(previous, image))
        changed = True
        print(kube("rollout", "status", "deployment/" + NAME, "--timeout=180s"))
        current = snapshot()
        save("deployment-after.json", current)
        pods = json.loads(kube("get", "pods", "-l", "app=" + NAME, "-o", "json"))
        save("pods-after.json", pods)
        running = [p for p in pods["items"] if not p["metadata"].get("deletionTimestamp")]
        if not running or any(not s.get("ready") or s.get("restartCount") != 0 or
            s.get("imageID") != image for p in running for s in p["status"]["containerStatuses"]):
            raise ValueError("Pod readiness, digest or restart check failed")
        for p in running:
            health = kube("exec", p["metadata"]["name"], "--", "wget", "-qO-", "http://127.0.0.1:8081/healthz")
            if health.strip() != "ok":
                raise ValueError("Private health endpoint failed")
        env = dict(__import__("os").environ, MARKETING_ORIGIN="https://shopyxi.com", MARKETING_EVIDENCE_DIR=str(out / "live"))
        for script in ["tests/production-smoke.mjs", "tests/public.mjs"]:
            result = subprocess.run(["node", script], cwd=APP, env=env, capture_output=True, text=True, encoding="utf-8")
            (out / (Path(script).stem + "-live.log")).write_text(result.stdout + result.stderr, encoding="utf-8")
            print(result.stdout)
            if result.returncode:
                raise ValueError("Critical live verification failed: " + script)
        save("deployment-result.json", {"pass": True, "image": image, "sourceCommit": commit,
            "previousImage": previous_image, "rollbackRequired": False, "rollbackRetained": True})
    except BaseException as failure:
        if changed:
            current = snapshot()
            if current["metadata"]["uid"] != previous["metadata"]["uid"] or current["spec"]["template"]["spec"]["containers"][0]["image"] != image:
                raise RuntimeError("Deployment changed concurrently; manual scoped recovery required") from failure
            print(set_image(current, previous_image))
            print(kube("rollout", "status", "deployment/" + NAME, "--timeout=180s"))
            restored = snapshot()
            save("deployment-restored.json", restored)
            if restored.get("status", {}).get("readyReplicas") != previous["spec"]["replicas"]:
                raise RuntimeError("Rollback readiness failed") from failure
            restored_public = {path: public_hash(path) for path in previous_public}
            save("public-after-rollback.json", restored_public)
            if restored_public != previous_public:
                raise RuntimeError("Rollback public artifact verification failed") from failure
            save("deployment-result.json", {"pass": False, "failure": str(failure),
                "previousImage": previous_image, "rollbackRequired": True, "rollbackHealthy": True})
        raise


if __name__ == "__main__":
    parser = argparse.ArgumentParser()
    parser.add_argument("--host", default="shopixy-vps")
    parser.add_argument("--evidence", required=True)
    deploy(parser.parse_args())
