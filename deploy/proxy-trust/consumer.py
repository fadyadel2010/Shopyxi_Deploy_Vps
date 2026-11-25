"""Named-ConfigMap poller; request-time NGINX expiry remains authoritative if this dies."""
import hashlib
import json
import os
import pathlib
import tempfile
import time

from reconciler import Kubernetes, NAMESPACE, PEER_CONFIGMAP, require


def validate(cm, anchors, now):
    require(cm["metadata"]["uid"] == anchors["peerConfigMapUid"] and
            not cm["metadata"].get("deletionTimestamp") and
            cm["metadata"].get("labels", {}).get("shopixy.io/proxy-trust") == "peers-v1")
    raw = cm["data"]["lease.json"]
    require(len(raw) <= 65536)
    envelope = json.loads(raw)
    require(set(envelope) == {"payload", "sha256"} and
            hashlib.sha256(envelope["payload"].encode()).hexdigest() == envelope["sha256"])
    payload = json.loads(envelope["payload"])
    require(payload["peerConfigMapUid"] == anchors["peerConfigMapUid"] and payload["schema"] == 1 and
            payload["issuedAt"] <= now + 1000 and now < payload["expiresAt"] and
            0 < payload["expiresAt"] - payload["issuedAt"] <= 20000)
    return raw.encode()


def publish(path, content):
    path.parent.mkdir(parents=True, exist_ok=True)
    fd, temp = tempfile.mkstemp(dir=path.parent, prefix=".lease-")
    try:
        with os.fdopen(fd, "wb") as stream:
            stream.write(content)
            stream.flush()
            os.fsync(stream.fileno())
        os.chmod(temp, 0o444)
        os.replace(temp, path)
    finally:
        if os.path.exists(temp):
            os.unlink(temp)


def localize(content, previous=None, now=None, uptime=None, boot=None):
    """Bind the controller deadline to this node's suspend-aware monotonic clock.

    Re-reading the same lease MUST NOT extend its local deadline. NGINX shares
    the pod's boot/uptime clock, so wall-clock rollback or worker restart cannot
    resurrect an already-published lease.
    """
    now = int(time.time() * 1000) if now is None else now
    uptime = int(time.clock_gettime(time.CLOCK_BOOTTIME) * 1000) if uptime is None else uptime
    boot = pathlib.Path("/proc/sys/kernel/random/boot_id").read_text().strip() if boot is None else boot
    envelope = json.loads(content)
    payload = json.loads(envelope["payload"])
    require(0 < payload["expiresAt"] - now <= 20000)
    deadline = uptime + payload["expiresAt"] - now
    if previous is not None:
        old = json.loads(previous)
        local = old["local"]
        require(local["bootId"] == boot and uptime >= local["observedUptimeMs"] and
                abs((now - local["observedWallMs"]) - (uptime - local["observedUptimeMs"])) <= 2000)
        if old["sha256"] == envelope["sha256"]:
            deadline = min(deadline, local["expiresUptimeMs"])
    require(uptime < deadline)
    envelope["local"] = {"bootId": boot, "observedWallMs": now,
                         "observedUptimeMs": uptime, "expiresUptimeMs": deadline}
    return json.dumps(envelope, separators=(",", ":")).encode()


def main():
    anchors = json.loads(pathlib.Path("/etc/proxy-trust/anchors.json").read_text())
    path = pathlib.Path("/run/proxy-trust/lease.json")
    api = Kubernetes()
    previous = path.read_bytes() if path.is_file() else None
    while True:
        start = time.monotonic()
        try:
            cm = api.get(f"/api/v1/namespaces/{NAMESPACE}/configmaps/{PEER_CONFIGMAP}")
            content = validate(cm, anchors, int(time.time() * 1000))
            content = localize(content, previous)
            publish(path, content)
            previous = content
        except Exception:
            # Immediate local revocation when possible; request-time expiry also
            # handles this process dying before unlink or a hung API/file call.
            try:
                path.unlink(missing_ok=True)
            except OSError:
                pass
        time.sleep(max(0, 1 - (time.monotonic() - start)))


if __name__ == "__main__":
    main()
