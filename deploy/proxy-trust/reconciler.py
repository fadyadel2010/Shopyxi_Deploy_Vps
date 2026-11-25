"""Finite endpoint enrollment. No Secret reads, Pod exec or workload mutations.

Polling is intentional: every renewal requires a complete fresh API read.
A stalled/failed watch cannot renew a lease. Consumers check the 20s deadline
on every request, independently of projection, this process and reload helpers.
"""
import concurrent.futures
import hashlib
import ipaddress
import json
import os
import pathlib
import ssl
import time
import urllib.parse
import urllib.request
import uuid

ROLES = ("traefik", "web", "storefront", "commercehub")
PEER_CONFIGMAP = "shopixy-proxy-trust-peers-v1"
NAMESPACE = "shopixy-app"


class Rejected(ValueError):
    pass


class NoRedirect(urllib.request.HTTPRedirectHandler):
    def redirect_request(self, req, fp, code, msg, headers, newurl):
        raise Rejected("Kubernetes API redirects are forbidden")


def require(condition):
    if not condition:
        raise Rejected("Endpoint enrollment rejected; values withheld")


def metadata(obj, name, namespace, uid=None):
    m = obj["metadata"]
    require(m["name"] == name and m.get("namespace") == namespace and
            m.get("uid") and not m.get("deletionTimestamp"))
    if uid is not None:
        require(m["uid"] == uid)
    return m


def owner(obj, kind, name, uid):
    refs = obj["metadata"].get("ownerReferences", [])
    require(len(refs) == 1 and refs[0].get("controller") is True and
            refs[0].get("kind") == kind and refs[0].get("name") == name and
            refs[0].get("uid") == uid)


def enroll(role, anchor, get):
    require(role in ROLES and anchor["service"] != "shopixy-api")
    ns, name = anchor["namespace"], anchor["service"]
    service = get(f"/api/v1/namespaces/{ns}/services/{name}")
    metadata(service, name, ns, anchor["serviceUid"])
    require(service["spec"].get("selector") == anchor["selector"])
    deployment = get(f"/apis/apps/v1/namespaces/{ns}/deployments/{name}")
    metadata(deployment, name, ns, anchor["deploymentUid"])
    require(type(deployment["spec"]["replicas"]) is int and deployment["spec"]["replicas"] == 1 and
            deployment["spec"]["selector"] == {"matchLabels": anchor["selector"]})
    selector = urllib.parse.urlencode({"labelSelector": "kubernetes.io/service-name=" + name})
    collection = get(f"/apis/discovery.k8s.io/v1/namespaces/{ns}/endpointslices?{selector}")
    require(not collection.get("metadata", {}).get("continue"))
    endpoints = []
    for slice_ in collection["items"]:
        sm = slice_["metadata"]
        require(sm.get("namespace") == ns and sm.get("uid") and not sm.get("deletionTimestamp") and
                sm.get("labels", {}).get("kubernetes.io/service-name") == name and
                sm.get("labels", {}).get("endpointslice.kubernetes.io/managed-by") ==
                "endpointslice-controller.k8s.io" and slice_["addressType"] == "IPv4")
        owner(slice_, "Service", name, anchor["serviceUid"])
        for endpoint in slice_["endpoints"]:
            endpoints.append((slice_, endpoint))
    # Reject extra/terminating/non-ready endpoints rather than filtering them out.
    require(len(endpoints) == 1)
    slice_, e = endpoints[0]
    require(e.get("conditions", {}).get("ready") is True and
            e.get("conditions", {}).get("terminating") is False and
            e.get("conditions", {}).get("serving", True) is True and len(e["addresses"]) == 1)
    ip = ipaddress.ip_address(e["addresses"][0])
    require(ip.version == 4 and str(ip) == e["addresses"][0] and
            not ip.is_loopback and not ip.is_unspecified and not ip.is_multicast and
            str(ip) not in service["spec"].get("clusterIPs", []) and
            str(ip) != service["spec"].get("clusterIP"))
    target = e["targetRef"]
    require(target["kind"] == "Pod" and target["namespace"] == ns and target.get("uid"))
    pod = get(f"/api/v1/namespaces/{ns}/pods/{urllib.parse.quote(target['name'], safe='')}")
    pm = metadata(pod, target["name"], ns, target["uid"])
    require(all(pm.get("labels", {}).get(k) == v for k, v in anchor["selector"].items()) and
            not pod["spec"].get("hostNetwork", False) and pod["status"].get("phase") == "Running" and
            pod["status"].get("podIP") == str(ip) and
            [c.get("status") for c in pod["status"].get("conditions", []) if c.get("type") == "Ready"] == ["True"])
    refs = pm.get("ownerReferences", [])
    require(len(refs) == 1 and refs[0].get("kind") == "ReplicaSet" and refs[0].get("controller") is True)
    rsref = refs[0]
    rs = get(f"/apis/apps/v1/namespaces/{ns}/replicasets/{urllib.parse.quote(rsref['name'], safe='')}")
    metadata(rs, rsref["name"], ns, rsref["uid"])
    owner(rs, "Deployment", name, anchor["deploymentUid"])
    return {"role": role, "namespace": ns, "service": name,
            "serviceUid": anchor["serviceUid"], "deploymentUid": anchor["deploymentUid"],
            "podUid": target["uid"], "podName": target["name"],
            "sliceUid": slice_["metadata"]["uid"], "ip": str(ip),
            "ready": True, "terminating": False}


def make_lease(anchors, get, clock=time.time):
    started = clock()
    with concurrent.futures.ThreadPoolExecutor(max_workers=4) as pool:
        futures = [pool.submit(enroll, role, anchors["workloads"][role], get) for role in ROLES]
        peers = [future.result() for future in futures]
    require(clock() - started <= 2 and len({p["ip"] for p in peers}) == 4)
    # Deadline is measured from BEFORE reads, never after a slow API operation.
    payload = json.dumps({"schema": 1, "sequence": str(uuid.uuid4()),
                          "peerConfigMapUid": anchors["peerConfigMapUid"],
                          "issuedAt": int(started * 1000), "expiresAt": int(started * 1000) + 20000,
                          "peers": peers}, separators=(",", ":"), sort_keys=True)
    return {"payload": payload, "sha256": hashlib.sha256(payload.encode()).hexdigest()}


class Kubernetes:
    def __init__(self):
        base = pathlib.Path("/var/run/secrets/kubernetes.io/serviceaccount")
        self.token_file = base / "token"  # Projected service-account token, not a Secret GET.
        self.context = ssl.create_default_context(cafile=str(base / "ca.crt"))
        self.opener = urllib.request.build_opener(urllib.request.ProxyHandler({}), NoRedirect(),
                                                 urllib.request.HTTPSHandler(context=self.context))
        self.origin = "https://" + os.environ["KUBERNETES_SERVICE_HOST"] + ":" + os.environ.get(
            "KUBERNETES_SERVICE_PORT_HTTPS", "443")

    def request(self, method, path, body=None):
        headers = {"Authorization": "Bearer " + self.token_file.read_text().strip()}
        if body is not None:
            headers["Content-Type"] = "application/merge-patch+json"
        request = urllib.request.Request(self.origin + path, method=method, headers=headers,
                                         data=json.dumps(body).encode() if body is not None else None)
        with self.opener.open(request, timeout=0.25) as response:
            require(int(response.headers.get("Content-Length", "0")) < 2_000_000)
            raw = response.read(2_000_001)
            require(len(raw) <= 2_000_000)
            return json.loads(raw)

    def get(self, path):
        return self.request("GET", path)

    def publish(self, envelope):
        path = f"/api/v1/namespaces/{NAMESPACE}/configmaps/{PEER_CONFIGMAP}"
        cm = self.get(path)
        require(cm["metadata"]["uid"] == self.configmap_uid and not cm["metadata"].get("deletionTimestamp"))
        require(cm["metadata"].get("labels", {}).get("shopixy.io/proxy-trust") == "peers-v1")
        return self.request("PATCH", path, {"metadata": {"uid": self.configmap_uid,
                                                       "resourceVersion": cm["metadata"]["resourceVersion"]},
                                           "data": {"lease.json": json.dumps(envelope, separators=(",", ":"))}})


def main():
    anchors = json.loads(pathlib.Path("/etc/proxy-trust/anchors.json").read_text())
    require(set(anchors["workloads"]) == set(ROLES))
    api = Kubernetes()
    api.configmap_uid = anchors["peerConfigMapUid"]
    while True:
        started = time.monotonic()
        try:
            api.publish(make_lease(anchors, api.get))
            print('{"event":"proxy-trust-renewed"}', flush=True)
        except Exception:
            # Never heartbeat old observations and never log response/credential values.
            print('{"event":"proxy-trust-renewal-rejected"}', flush=True)
        time.sleep(max(0, 1 - (time.monotonic() - started)))


if __name__ == "__main__":
    main()
