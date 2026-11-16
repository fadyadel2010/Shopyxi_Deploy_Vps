"""Read-only ingress/certificate metadata and aggregate counts; no Secret reads."""
import datetime
import json
import re
import subprocess
from pathlib import Path


def remote(command, data=None):
    result = subprocess.run(["ssh", "-o", "BatchMode=yes", "shopixy-vps", command],
                            input=data.encode() if data is not None else None, capture_output=True, check=True)
    return result.stdout.decode("utf-8").replace("\r\n", "\n")


def kube(args):
    return json.loads(remote("kubectl " + args + " -o json"))


controllers = [{
    "namespace": d["metadata"]["namespace"], "name": d["metadata"]["name"],
    "generation": d["metadata"]["generation"], "readyReplicas": d["status"].get("readyReplicas", 0),
    "images": [c["image"] for c in d["spec"]["template"]["spec"]["containers"]],
} for d in kube("get deployments -A")["items"]
    if "caddy" in d["metadata"]["name"].lower() or d["metadata"]["name"] in
    ("traefik", "cert-manager", "cert-manager-cainjector", "cert-manager-webhook")]

certificates = [{
    "name": c["metadata"]["name"], "namespace": c["metadata"]["namespace"],
    "dnsNames": c["spec"].get("dnsNames"), "issuerRef": c["spec"]["issuerRef"],
    "generation": c["metadata"]["generation"],
    "conditions": c["status"].get("conditions"), "notAfter": c["status"].get("notAfter"),
    "renewalTime": c["status"].get("renewalTime"), "revision": c["status"].get("revision"),
} for c in kube("get certificates -n shopixy-app")["items"]
    if c["metadata"]["name"] in ("shopixy-api-tls", "shopixy-frontends-app82-r5",
                                "shopixy-public-entrypoints-cutover-r1", "shopixy-wildcard-origin-r1")]

routes = [{
    "name": r["metadata"]["name"], "entryPoints": r["spec"].get("entryPoints"),
    "routes": [{"match": s["match"], "services": [{"name": d["name"], "port": d.get("port")} for d in s.get("services", [])]}
               for s in r["spec"].get("routes", [])],
    "tls": r["spec"].get("tls"),
} for r in kube("get ingressroutes.traefik.io -n shopixy-app")["items"]
    if r["metadata"]["name"] in ("app82-frontend-websecure-r5", "cloudflare-cutover-existing-frontends-api-r1",
                                "shopixy-tenant-wildcard-https-r1")]

issuers = [{
    "name": i["metadata"]["name"], "conditions": i.get("status", {}).get("conditions"),
    "acmeServer": i.get("spec", {}).get("acme", {}).get("server"),
    "solverTypes": [["dns01" if "dns01" in s else "http01" if "http01" in s else "other"]
                    for s in i.get("spec", {}).get("acme", {}).get("solvers", [])],
} for i in kube("get clusterissuers")["items"]
    if i["metadata"]["name"] in {c["issuerRef"]["name"] for c in certificates}]

sql = """
BEGIN TRANSACTION READ ONLY;
SELECT json_build_object('kind','domains','byTypeAndLifecycle',COALESCE(json_agg(r),'[]'::json))
FROM (SELECT "DomainType" AS domain_type,"LifecycleState" AS lifecycle_state,COUNT(*) AS count
 FROM "Domains" WHERE NOT "IsDeleted" GROUP BY "DomainType","LifecycleState") r;
SELECT json_build_object('kind','adapter_certificates','byStatus',COALESCE(json_agg(r),'[]'::json))
FROM (SELECT "Status" AS status,COUNT(*) AS count FROM "SslCertificates"
 WHERE NOT "IsDeleted" GROUP BY "Status") r;
SELECT json_build_object('kind','adapter_operations','byTypeAndStatus',COALESCE(json_agg(r),'[]'::json))
FROM (SELECT "OperationType" AS operation_type,"Status" AS status,COUNT(*) AS count
 FROM "SslCertificateOperations" WHERE NOT "IsDeleted" GROUP BY "OperationType","Status") r;
SELECT json_build_object('kind','existing_product_search_flags','data',row_to_json(r))
FROM (SELECT "Id" AS id,"IsActive" AS is_active,"IsOnlineVisible" AS is_online_visible,
 "IsSearchable" AS is_searchable,"PublishStatus" AS publish_status,"IsDeleted" AS is_deleted
 FROM "Products" WHERE "Id"='b2ec52fb-d600-f1ed-dc23-2fcdc09e61b8') r;
COMMIT;
"""
sql_result = remote("kubectl exec -i -n shopixy-data shopixy-postgres-1 -c postgres -- "
                    "psql -X -qAt -v ON_ERROR_STOP=1 -U postgres -d app", sql)
pod = kube("get pods -n shopixy-app -l app=shopixy-api")["items"][0]["metadata"]["name"]
# Allowlist numerical fields only; do not persist log lines.
logs = remote("kubectl logs -n shopixy-app " + pod + " -c api --since=20m")
search_diagnostics = [
    dict(zip(("sqlVisibleCount", "mongoPublishedCount", "elasticsearchCount", "syncedFlagCount", "ratio"), m.groups()))
    for m in re.finditer(r"CommerceHub search sync unhealthy \| SqlVisible: (\d+) \| MongoPublished: (\d+) \| Elasticsearch: (\d+) \| SyncedFlagCount: (\d+) \| Ratio: ([0-9.]+)", logs)
]
for line in logs.splitlines():
    try:
        entry = json.loads(line)
    except (ValueError, TypeError):
        continue
    if "CommerceHub search sync unhealthy" not in line:
        continue
    values = {}
    def collect_numbers(value):
        if not isinstance(value, dict):
            return
        for key, item in value.items():
            if key in ("SqlVisibleCount", "MongoPublishedCount", "ElasticsearchCount", "SyncedFlagCount", "Ratio") and isinstance(item, (int, float)):
                values[key] = item
            elif isinstance(item, dict):
                collect_numbers(item)
    collect_numbers(entry)
    if values:
        search_diagnostics.append(values)
public_http = {}
for host in ("admin.shopyxi.com", "api.shopyxi.com", "ahram.shopyxi.com"):
    public_http[host] = remote("curl -sS -o /dev/null --max-time 30 -w '%{http_code}' https://" + host + "/").strip()
result = {
    "verifiedAtUtc": datetime.datetime.now(datetime.timezone.utc).isoformat(), "readOnly": True,
    "controllers": controllers, "certificates": certificates, "issuers": issuers, "routes": routes,
    "adapterWorkloadCounts": [json.loads(s) for s in sql_result.splitlines() if s.startswith("{")],
    "searchSyncDiagnostics": search_diagnostics, "publicHttpsStatus": public_http,
    "hostCaddyProcessPresent": bool(remote("ps -eo comm").lower().splitlines().count("caddy")),
}
out = Path(__file__).resolve().parent / "health-architecture.json"
out.write_text(json.dumps(result, indent=2, ensure_ascii=False) + "\n", encoding="utf-8")
print(json.dumps(result, indent=2))
