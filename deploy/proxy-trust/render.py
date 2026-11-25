"""Offline versioned successor generation. Never contacts a cluster or reads Secrets."""
import argparse
import json
import pathlib

ROOT = pathlib.Path(__file__).resolve().parents[2]
EVIDENCE = ROOT / "docs/marketing/phase-4.1-proxy-trust-diagnosis-20261122-01/evidence"
CONFIGS = {
    "api": "shopixy-api-clientip-edge-wildcard-r1",
    "web": "shopixy-web-cloudflare-cutover-r1",
    "storefront": "shopixy-storefront-wildcard-r1",
    "commercehub": "shopixy-commercehub-app82-r4",
}
ALIASES = ("CF-Connecting-IP", "Forwarded", "X-Real-IP", "True-Client-IP",
           "X-Original-For", "X-Original-Proto", "X-Original-Host",
           "X-Forwarded-Scheme", "X-Forwarded-Ssl", "Front-End-Https")


def nginx(role, original):
    lines = original.splitlines()
    # The trust module replaces real-IP rewriting, rather than using rewritten
    # remote_addr as immediate-peer evidence. All host/cache/guard blocks remain.
    lines = [line for line in lines if not line.strip().startswith((
        "set_real_ip_from ", "real_ip_header ", "real_ip_recursive ",
        "map $http_x_forwarded_proto $trusted_proto ",
        "# Accepted Traefik pod authority plus Cloudflare"))]
    text = "\n".join(lines) + "\n"
    text = "load_module /usr/lib/nginx/modules/ngx_http_js_module.so;\n" + text
    declarations = f"""
    js_import trust from /etc/proxy-trust/normalizer.mjs;
    js_var $trust_role {role};
    js_var $trust_anchors /etc/proxy-trust/anchors.json;
    js_var $trust_lease /run/proxy-trust/lease.json;
    js_var $trust_result "";
    js_set $trust_status trust.status;
    js_set $trust_client trust.client;
    js_set $trust_scheme trust.scheme;
    js_set $trust_host trust.authority;
"""
    text = text.replace("http {", "http {" + declarations, 1)
    text = text.replace("$binary_remote_addr", "$trust_client")
    # Every fallback emits a single canonical value, never a raw incoming list.
    for old, new in [("X-Forwarded-Proto $scheme", "X-Forwarded-Proto $trust_scheme"),
                     ("X-Forwarded-Proto $trusted_proto", "X-Forwarded-Proto $trust_scheme"),
                     ("X-Forwarded-For $remote_addr", "X-Forwarded-For $trust_client"),
                     ("X-Forwarded-Host $http_host", "X-Forwarded-Host $trust_host"),
                     ("X-Forwarded-Host $host", "X-Forwarded-Host $trust_host"),
                     ("Host $http_host", "Host $trust_host"), ("Host $host", "Host $trust_host")]:
        text = text.replace("proxy_set_header " + old + ";", "proxy_set_header " + new + ";")
    gate = """
            if ($trust_status = 400) { return 400; }
            if ($trust_status = 403) { return 403; }
            if ($trust_status = 503) { return 503; }
"""
    if role == "api":
        text = text.replace("listen 18080;", "listen 18080;" + gate, 1)
    else:
        text = text.replace("location ~ ^/(api|hubs)(/|$) {", "location ~ ^/(api|hubs)(/|$) {" + gate, 1)
    # Explicitly clear aliases at EVERY proxy_pass-bearing location.
    strip = "\n".join('            proxy_set_header ' + name + ' "";' for name in ALIASES)
    for name in ALIASES:
        text = "\n".join(line for line in text.splitlines()
                         if line.strip() != f'proxy_set_header {name} "";') + "\n"
    text = text.replace("proxy_http_version 1.1;", "proxy_http_version 1.1;\n" + strip)
    return text


def anchors(peer_uid):
    observations = json.loads((EVIDENCE / "deployment-service-metadata.json").read_text())
    official = json.loads((EVIDENCE / "official-reference-evidence.json").read_text())
    result = {"peerConfigMapUid": peer_uid, "cloudflare": official["officialRanges"], "workloads": {}}
    for role in ["traefik", "web", "storefront", "commercehub"]:
        name = "traefik" if role == "traefik" else "shopixy-" + role
        ns = "kube-system" if role == "traefik" else "shopixy-app"
        svc = next(x for x in observations["services"] if x["namespace"] == ns and x["name"] == name)
        dep = next(x for x in observations["deployments"] if x["namespace"] == ns and x["name"] == name)
        result["workloads"][role] = {"service": name, "namespace": ns, "serviceUid": svc["uid"],
                                     "deploymentUid": dep["uid"], "selector": svc["spec"]["selector"]}
    return result


def render(out, peer_uid):
    out.mkdir(parents=True, exist_ok=True)
    configs = {x["name"]: x for x in json.loads((EVIDENCE / "current-proxy-configuration.json").read_text())["configs"]}
    resources = []
    for role, name in CONFIGS.items():
        content = nginx(role, configs[name]["nginxConfig"])
        (out / (role + ".conf")).write_text(content)
        resources.append({"apiVersion": "v1", "kind": "ConfigMap",
                          "metadata": {"name": "shopixy-" + role + "-proxy-trust-20261122-v1", "namespace": "shopixy-app"},
                          "immutable": True, "data": {"nginx.conf": content}})
    (out / "nginx-successors.json").write_text(json.dumps({"apiVersion": "v1", "kind": "List", "items": resources}, indent=2))
    (out / "anchors.json").write_text(json.dumps(anchors(peer_uid), separators=(",", ":")))


if __name__ == "__main__":
    parser = argparse.ArgumentParser()
    parser.add_argument("--output", required=True, type=pathlib.Path)
    parser.add_argument("--peer-configmap-uid", required=True)
    args = parser.parse_args()
    if not args.peer_configmap_uid or args.peer_configmap_uid.startswith("REQUIRED"):
        parser.error("Explicit approved enrollment UID required; placeholders cannot enroll authority")
    render(args.output, args.peer_configmap_uid)
