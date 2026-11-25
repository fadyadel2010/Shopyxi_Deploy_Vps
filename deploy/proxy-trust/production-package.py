"""Offline minimal patch/package generator. NO Kubernetes client, apply or rollout.

The operator supplies fresh sanitized metadata and approved immutable image refs.
An unbuilt image, unknown API assembly provenance or placeholder cannot be promoted.
Private JSON settings are transformed only in operator custody, never printed here.
"""
import argparse
import copy
import datetime
import hashlib
import ipaddress
import json
import pathlib
import re
import sys

sys.path.insert(0, str(pathlib.Path(__file__).parent))
import render


def immutable(value):
    if not re.fullmatch(r"[A-Za-z0-9./:_-]+@sha256:[a-f0-9]{64}", value):
        raise ValueError("A built, approved immutable image reference is required")
    return value


def settings_successor(source):
    """Pure in-memory transform; caller owns protected settings custody."""
    result = copy.deepcopy(source)
    result["metadata"] = {"name": "shopixy-api-json-proxy-trust-20261122-v1", "namespace": "shopixy-app"}
    result["immutable"] = True
    for name, text in result["data"].items():
        if name in ["appsettings.json", "appsettings.Production.json"]:
            parsed = json.loads(text)
            forwarded = parsed.setdefault("ForwardedHeaders", {})
            forwarded["KnownProxies"] = ["127.0.0.1", "::1"]
            forwarded.pop("KnownNetworks", None)
            forwarded.pop("KnownIPNetworks", None)
            parsed["ASPNETCORE_FORWARDEDHEADERS_ENABLED"] = False
            result["data"][name] = json.dumps(parsed, separators=(",", ":"))
    return result


SECURITY = {"runAsNonRoot": True, "runAsUser": 65532, "runAsGroup": 65532,
            "privileged": False, "allowPrivilegeEscalation": False, "readOnlyRootFilesystem": True,
            "capabilities": {"drop": ["ALL"], "add": []}}


def lease_init(runtime):
    # Do NOT set pod fsGroup: that could change ownership on existing business PVCs.
    return {"name": "proxy-trust-volume-owner", "image": runtime,
            "command": ["/usr/bin/python3", "-B", "-c",
                        "import os; os.chmod('/run/proxy-trust',0o755); os.chown('/run/proxy-trust',65532,65532)"],
            "securityContext": {"runAsUser": 0, "runAsGroup": 0, "runAsNonRoot": False,
                                "allowPrivilegeEscalation": False, "readOnlyRootFilesystem": True,
                                "capabilities": {"drop": ["ALL"], "add": ["CHOWN"]}},
            "resources": {"requests": {"cpu": "5m", "memory": "16Mi"},
                          "limits": {"cpu": "100m", "memory": "32Mi"}},
            "volumeMounts": [{"name": "proxy-trust-lease", "mountPath": "/run/proxy-trust"}]}


def patches(runtime, api_image, assets):
    immutable(runtime)
    immutable(api_image)
    for image in assets.values():
        immutable(image)
    output = {}
    for role in ["api", "web", "storefront", "commercehub"]:
        deployment = "shopixy-" + role
        name = "clientip-edge" if role == "api" else role
        consumer = {"name": "proxy-trust-consumer", "image": runtime,
                    "command": ["/usr/bin/python3", "-B", "/opt/proxy-trust/consumer.py"],
                    "securityContext": copy.deepcopy(SECURITY),
                    "resources": {"requests": {"cpu": "5m", "memory": "24Mi"},
                                  "limits": {"cpu": "100m", "memory": "64Mi"}},
                    "volumeMounts": [
                        {"name": "proxy-trust-anchors", "mountPath": "/etc/proxy-trust/anchors.json",
                         "subPath": "anchors.json", "readOnly": True},
                        {"name": "proxy-trust-lease", "mountPath": "/run/proxy-trust"},
                        {"name": "proxy-trust-api-token", "mountPath": "/var/run/secrets/kubernetes.io/serviceaccount",
                         "readOnly": True}]}
        proxy = {"name": name, "image": runtime, "command": ["/usr/sbin/nginx", "-g", "daemon off;"],
                 "securityContext": copy.deepcopy(SECURITY), "args": [], "volumeMounts": [
                     {"name": "proxy-trust-anchors", "mountPath": "/etc/proxy-trust/anchors.json",
                      "subPath": "anchors.json", "readOnly": True},
                     {"name": "proxy-trust-lease", "mountPath": "/run/proxy-trust", "readOnly": True}]}
        pod = {"containers": [proxy, consumer], "initContainers": [lease_init(runtime)],
               "volumes": [
                   {"name": "proxy-trust-anchors", "configMap": {"name": "shopixy-proxy-trust-anchors-20261122-v1"}},
                   {"name": "proxy-trust-lease", "emptyDir": {"medium": "Memory", "sizeLimit": "1Mi"}},
                   {"name": "proxy-trust-api-token", "projected": {"sources": [
                       {"serviceAccountToken": {"path": "token", "expirationSeconds": 600}},
                       {"configMap": {"name": "kube-root-ca.crt", "items": [{"key": "ca.crt", "path": "ca.crt"}]}}
                   ]}}]}
        # Existing nginx.conf mount names are retained; ONLY their referenced
        # immutable ConfigMap is replaced. All other volumes/mounts remain.
        config_volume = "clientip-edge-settings" if role == "api" else "settings"
        pod["volumes"].append({"name": config_volume,
                               "configMap": {"name": "shopixy-" + role + "-proxy-trust-20261122-v1"}})
        if role == "api":
            # This distinct new binary is necessary to enforce the all-provider
            # loopback invariant. Do not invent parity with a running business image.
            pod["containers"].append({"name": "api", "image": api_image, "env": [
                {"name": "ForwardedHeaders__KnownProxies__0", "value": "127.0.0.1"},
                {"name": "ForwardedHeaders__KnownProxies__1", "value": "::1"},
                {"name": "ASPNETCORE_FORWARDEDHEADERS_ENABLED", "value": "false"}]})
            pod["volumes"].append({"name": "settings", "configMap": {
                "name": "shopixy-api-json-proxy-trust-20261122-v1"}})
            for probe in ["livenessProbe", "readinessProbe", "startupProbe"]:
                proxy[probe] = {"exec": {"command": [
                    "/usr/bin/python3", "-c",
                    "import urllib.request; assert urllib.request.urlopen('http://127.0.0.1:18081',timeout=1).status==204"]},
                    "timeoutSeconds": 2, "periodSeconds": 10, "failureThreshold": 12}
        else:
            pod["volumes"].append({"name": "proxy-trust-static", "emptyDir": {}})
            proxy["volumeMounts"].append({"name": "proxy-trust-static", "mountPath": "/usr/share/nginx/html", "readOnly": True})
            # No application image is rebuilt. The exact original static bytes
            # are copied by its own original image, not reconstructed from source.
            pod["initContainers"].append({"name": "preserve-static-assets", "image": assets[role],
                                      "command": ["/bin/sh", "-ec", "cp -a /usr/share/nginx/html/. /preserved/"],
                                      "securityContext": {
                                          "runAsUser": 0, "runAsGroup": 0, "runAsNonRoot": False,
                                          "allowPrivilegeEscalation": False, "readOnlyRootFilesystem": True,
                                          "capabilities": {"drop": ["ALL"], "add": ["CHOWN", "FOWNER", "DAC_OVERRIDE"]}},
                                      "resources": {"requests": {"cpu": "10m", "memory": "32Mi"},
                                                    "limits": {"cpu": "200m", "memory": "64Mi"}},
                                      "volumeMounts": [{"name": "proxy-trust-static", "mountPath": "/preserved"}]})
        output[deployment] = {"apiVersion": "apps/v1", "kind": "Deployment",
                              "metadata": {"name": deployment, "namespace": "shopixy-app"},
                              "spec": {"template": {"spec": pod}}}
    return output


def control_resources(runtime, anchors, api_destinations, service_accounts):
    """Complete versioned controller + explicitly named consumer bindings.

    Egress is exact operator-approved /32+port, not node/pod/service CIDRs.
    Existing policies are additive: these resources never remove existing denies
    or repair a permissive competing policy. CNI qualification is mandatory.
    """
    immutable(runtime)
    if set(service_accounts) != {"api", "web", "storefront", "commercehub"}:
        raise ValueError("Exact existing consumer ServiceAccounts required")
    for name in service_accounts.values():
        if not re.fullmatch(r"[a-z0-9](?:[-a-z0-9.]*[a-z0-9])?", name):
            raise ValueError("Malformed ServiceAccount")
    peers = []
    for endpoint in api_destinations:
        ip = ipaddress.ip_address(endpoint["ip"])
        if ip.version != 4 or ip.is_unspecified or ip.is_loopback or ip.is_multicast:
            raise ValueError("Exact Kubernetes API destination IPv4 required")
        if type(endpoint["port"]) is not int or endpoint["port"] not in [443, 6443]:
            raise ValueError("Only approved Kubernetes HTTPS API ports")
        peers.append({"to": [{"ipBlock": {"cidr": str(ip) + "/32"}}],
                      "ports": [{"protocol": "TCP", "port": endpoint["port"]}]})
    if not peers or len(peers) > 4:
        raise ValueError("One to four explicit API destination tuples required")
    items = json.loads((pathlib.Path(__file__).parent / "rbac.json").read_text())["items"]
    items += [
        {"apiVersion": "v1", "kind": "ConfigMap",
         "metadata": {"name": "shopixy-proxy-trust-anchors-20261122-v1", "namespace": "shopixy-app"},
         "immutable": True, "data": {"anchors.json": json.dumps(anchors, separators=(",", ":"))}},
        {"apiVersion": "rbac.authorization.k8s.io/v1", "kind": "RoleBinding",
         "metadata": {"name": "shopixy-proxy-trust-consume-v1", "namespace": "shopixy-app"},
         "roleRef": {"apiGroup": "rbac.authorization.k8s.io", "kind": "Role",
                     "name": "shopixy-proxy-trust-consume-v1"},
         "subjects": [{"kind": "ServiceAccount", "namespace": "shopixy-app", "name": name}
                      for name in sorted(set(service_accounts.values()))]}]
    mounts = [
        {"name": "proxy-trust-anchors", "mountPath": "/etc/proxy-trust/anchors.json",
         "subPath": "anchors.json", "readOnly": True},
        {"name": "proxy-trust-api-token", "mountPath": "/var/run/secrets/kubernetes.io/serviceaccount", "readOnly": True}]
    # Reuse only the explicitly named read-only anchors and projected-token volumes.
    volumes = patches(runtime, runtime, {x: runtime for x in ["web", "storefront", "commercehub"]})[
        "shopixy-api"]["spec"]["template"]["spec"]["volumes"][:3]
    volumes = [v for v in volumes if v["name"] != "proxy-trust-lease"]
    items.append({
        "apiVersion": "apps/v1", "kind": "Deployment",
        "metadata": {"name": "shopixy-proxy-trust-reconciler-v1", "namespace": "shopixy-app"},
        "spec": {"replicas": 1, "strategy": {"type": "Recreate"},
                 "selector": {"matchLabels": {"app": "shopixy-proxy-trust-reconciler-v1"}},
                 "template": {"metadata": {"labels": {"app": "shopixy-proxy-trust-reconciler-v1"}},
                              "spec": {"serviceAccountName": "shopixy-proxy-trust-reconciler",
                                       "automountServiceAccountToken": False, "volumes": volumes,
                                       "containers": [{
                                           "name": "reconciler", "image": runtime,
                                           "command": ["/usr/bin/python3", "-B", "/opt/proxy-trust/reconciler.py"],
                                           "securityContext": copy.deepcopy(SECURITY), "volumeMounts": mounts,
                                           "resources": {"requests": {"cpu": "10m", "memory": "32Mi"},
                                                         "limits": {"cpu": "200m", "memory": "96Mi"}}}]}}}})
    for role in ["reconciler", "api", "web", "storefront", "commercehub"]:
        app = "shopixy-proxy-trust-reconciler-v1" if role == "reconciler" else "shopixy-" + role
        spec = {"podSelector": {"matchLabels": {"app": app}}, "policyTypes": ["Egress"],
                "egress": copy.deepcopy(peers)}
        if role == "reconciler":
            spec["policyTypes"].append("Ingress")
            spec["ingress"] = []
        items.append({"apiVersion": "networking.k8s.io/v1", "kind": "NetworkPolicy",
                      "metadata": {"name": "shopixy-proxy-trust-" + role + "-api-egress-v1",
                                   "namespace": "shopixy-app"}, "spec": spec})
    return {"apiVersion": "v1", "kind": "List", "items": items}


def approved_anchors(value, peer_uid):
    if value.get("peerConfigMapUid") != peer_uid or not re.fullmatch(
            r"[a-f0-9]{8}(?:-[a-f0-9]{4}){3}-[a-f0-9]{12}", peer_uid):
        raise ValueError("Actual precreated peer ConfigMap UID required; no lab placeholder")
    if set(value.get("workloads", {})) != {"traefik", "web", "storefront", "commercehub"}:
        raise ValueError("Four exact owned workloads required")
    if value.get("cloudflare") != render.anchors("lab")["cloudflare"]:
        raise ValueError("Cloudflare restrictions must remain unchanged")
    for role, anchor in value["workloads"].items():
        expected = "traefik" if role == "traefik" else "shopixy-" + role
        if anchor["service"] != expected or anchor["namespace"] != (
                "kube-system" if role == "traefik" else "shopixy-app") or not anchor.get("selector"):
            raise ValueError("Enrollment must not change host/workload ownership")
        for key in ["serviceUid", "deploymentUid"]:
            if not re.fullmatch(r"[a-f0-9]{8}(?:-[a-f0-9]{4}){3}-[a-f0-9]{12}", anchor[key]):
                raise ValueError("Actual fresh ownership UID required")
    return value


if __name__ == "__main__":
    parser = argparse.ArgumentParser()
    parser.add_argument("--runtime-image", required=True, type=immutable)
    parser.add_argument("--api-image", required=True, type=immutable)
    parser.add_argument("--peer-configmap-uid", required=True)
    parser.add_argument("--output", required=True, type=pathlib.Path)
    parser.add_argument("--source-image-parity-receipt", required=True, type=pathlib.Path)
    parser.add_argument("--runtime-approval-receipt", required=True, type=pathlib.Path)
    parser.add_argument("--approved-enrollment", required=True, type=pathlib.Path,
                        help="Fresh sanitized receipt: status, anchors, apiDestinations, serviceAccounts, assets")
    args = parser.parse_args()
    receipt = json.loads(args.source_image_parity_receipt.read_text())
    policy_sha = hashlib.sha256((render.ROOT / "PointOfSale.API/Configuration/LoopbackForwardedHeadersPolicy.cs").read_bytes()).hexdigest()
    program_sha = hashlib.sha256((render.ROOT / "PointOfSale.API/Program.cs").read_bytes()).hexdigest()
    if (receipt.get("status") != "PASS" or receipt.get("image") != args.api_image or
            receipt.get("policySha256") != policy_sha or receipt.get("programSha256") != program_sha):
        parser.error("API image/source parity must be independently established, not inferred")
    runtime = json.loads(args.runtime_approval_receipt.read_text())
    runtime_sources = {name: hashlib.sha256((pathlib.Path(__file__).parent / name).read_bytes()).hexdigest()
                       for name in ["normalizer.mjs", "reconciler.py", "consumer.py", "Dockerfile"]}
    if (runtime.get("status") != "PASS" or runtime.get("productionApproved") is not True or
            runtime.get("image") != args.runtime_image or runtime.get("sources") != runtime_sources):
        parser.error("Qualified production runtime must be bound to these exact normalization sources")
    enrollment = json.loads(args.approved_enrollment.read_text())
    if enrollment.get("status") != "PASS":
        parser.error("Fresh enrollment and static-asset provenance approval required")
    observed = datetime.datetime.fromisoformat(enrollment["observedAtUtc"])
    age = (datetime.datetime.now(datetime.timezone.utc) - observed).total_seconds()
    if not 0 <= age <= 300:
        parser.error("Enrollment observation must be UTC-aware and at most five minutes old")
    anchors = approved_anchors(enrollment["anchors"], args.peer_configmap_uid)
    assets = enrollment["assets"]
    controls = control_resources(args.runtime_image, anchors, enrollment["apiDestinations"], enrollment["serviceAccounts"])
    if args.output.exists():
        parser.error("Refusing to overwrite an existing package")
    render.render(args.output, args.peer_configmap_uid)
    (args.output / "anchors.json").write_text(json.dumps(anchors, separators=(",", ":")))
    (args.output / "controller-rbac-egress.json").write_text(json.dumps(controls, indent=2))
    for name, document in patches(args.runtime_image, args.api_image, assets).items():
        (args.output / (name + ".strategic-merge-patch.json")).write_text(json.dumps(document, indent=2))
    print("Offline candidate only: no resources applied; policy/identity/image approval gates remain.")
