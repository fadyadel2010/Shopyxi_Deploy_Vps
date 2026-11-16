"""Read-only Production receipts. Never persist config bodies, env values or Secrets."""
import datetime
import hashlib
import json
import subprocess
import sys
from pathlib import Path

OUT = Path(__file__).resolve().parent


def remote(command, data=None):
    return subprocess.run(["ssh", "-o", "BatchMode=yes", "shopixy-vps", command],
                          input=data.encode("utf-8") if data is not None else None,
                          capture_output=True, check=True).stdout.decode("utf-8").replace("\r\n", "\n")


def kube(args):
    return json.loads(remote("kubectl " + args + " -o json"))


def save(name, data):
    (OUT / name).write_text(json.dumps(data, ensure_ascii=False, indent=2) + "\n", encoding="utf-8")


def digest(value):
    return hashlib.sha256(json.dumps(value, sort_keys=True, separators=(",", ":")).encode()).hexdigest()


phase = sys.argv[1]
deployments = kube("get deployments -n shopixy-app")["items"]
pods = kube("get pods -n shopixy-app")["items"]
workloads = []
for deployment in deployments:
    name = deployment["metadata"]["name"]
    if name not in ("shopixy-api", "shopixy-web", "shopixy-storefront"):
        continue
    containers = deployment["spec"]["template"]["spec"]["containers"]
    matching = [p for p in pods if p["metadata"]["labels"].get("app") == name
                and not p["metadata"].get("deletionTimestamp")]
    image = containers[0]["image"]
    inspection = json.loads(remote("/usr/local/bin/k3s crictl inspecti " + image))
    labels = inspection.get("info", {}).get("imageSpec", {}).get("config", {}).get("Labels", {})
    workloads.append({
        "name": name, "generation": deployment["metadata"]["generation"],
        "observedGeneration": deployment["status"]["observedGeneration"],
        "readyReplicas": deployment["status"].get("readyReplicas", 0),
        "specSha256": digest(deployment["spec"]),
        "containers": [{"name": c["name"], "image": c["image"]} for c in containers],
        "sourceCommit": labels.get("shopixy.io/source-commit"),
        "runtimeConfigId": inspection["status"]["id"],
        "pods": [{"name": p["metadata"]["name"], "uid": p["metadata"]["uid"],
                  "containers": [{k: c.get(k) for k in ("name", "ready", "restartCount", "imageID")}
                                 for c in p["status"]["containerStatuses"]]} for p in matching],
    })

api = next(w for w in workloads if w["name"] == "shopixy-api")
web = next(w for w in workloads if w["name"] == "shopixy-web")
prefix = "kubectl exec -n shopixy-app " + api["pods"][0]["name"] + " -c api -- "
health = {}
health_probe = """
import json, subprocess, urllib.request, urllib.error
p = subprocess.Popen(['kubectl','-n','shopixy-app','port-forward','service/shopixy-api',
                      '18081:8080','--address=127.0.0.1'],
                     stdout=subprocess.PIPE, stderr=subprocess.PIPE, text=True)
try:
    line = p.stdout.readline()
    if 'Forwarding from' not in line:
        raise RuntimeError('Read-only loopback health forwarding did not start')
    results = {}
    for endpoint in ('/health/ready','/health'):
        try:
            r = urllib.request.urlopen('http://127.0.0.1:18081'+endpoint, timeout=90)
        except urllib.error.HTTPError as error:
            r = error
        results[endpoint] = {'httpStatus':r.code,'body':json.loads(r.read())}
    print(json.dumps(results))
finally:
    p.terminate()
    p.wait(timeout=10)
"""
health_results = json.loads(remote("python3 -", health_probe))
for endpoint, response in health_results.items():
    body = response["body"]
    health[endpoint] = {
        "httpStatus": response["httpStatus"], "status": body["status"],
        "checks": {k: v["status"] for k, v in body.get("entries", {}).items()},
        "nonHealthyDescriptions": {k: {"status": v["status"], "description": v.get("description"),
                                      "exceptionCategory": v.get("exceptionCategory")}
                                  for k, v in body.get("entries", {}).items() if v["status"] != "Healthy"},
    }

config = []
for name in ("appsettings.json", "appsettings.Production.json"):
    body = json.loads(remote(prefix + "cat /app/" + name))
    config.append({"file": name, "sha256": digest(body),
                   "enabled": {section: body.get(section, {}).get("Enabled", "absent")
                               for section in ("CertificateProvider", "CaddyEdge")},
                   "adapterSettingPresence": {
                       section: {key: bool(body.get(section, {}).get(key))
                                 for key in keys}
                       for section, keys in {
                           "CertificateProvider": ["AccountReference", "DirectoryUrl"],
                           "CaddyEdge": ["ManagedRoutesDirectory", "StorefrontRoot", "ApiUpstream",
                                         "LogRoot", "ValidateCommand", "ReloadCommand"],
                       }.items()}})
# Read in memory and retain only allowlisted booleans plus names, never env values.
env = dict(line.split("=", 1) for line in remote(prefix + "printenv").splitlines() if "=" in line)
enabled_env = {s + "__Enabled": env.get(s + "__Enabled", "absent")
               for s in ("CertificateProvider", "CaddyEdge")}
assert all(v in ("absent", "true", "false", "True", "False") for v in enabled_env.values())
adapter_env_names = sorted(k for k in env if k.startswith(("CertificateProvider__", "CaddyEdge__")))
commandline = remote(prefix + "cat /proc/1/cmdline").split("\0")
config_override_arguments = [a.split("=", 1)[0] for a in commandline
                             if any(s.lower() in a.lower() for s in ("certificateprovider", "caddyedge"))]

sql = """
BEGIN TRANSACTION READ ONLY;
SELECT json_build_object('kind','commission_rules','rules',COALESCE(json_agg(r),'[]'::json))
FROM (SELECT "Id" AS id,"ScopeType" AS scope_type,"ScopeId" AS scope_id,"Rate" AS rate,
 "IsActive" AS is_active,"Priority" AS priority,"IsDeleted" AS is_deleted,"CreatedAt" AS created_at
 FROM "CommissionRules") r;
SELECT json_build_object('kind','listing','data',row_to_json(l))
FROM (SELECT "Id" AS id,"TenantId" AS tenant_id,"ProductId" AS product_id,"VariantId" AS variant_id,
 "CommerceHubCategoryId" AS category_id,"CommerceHubPrice" AS price,"IsPublished" AS is_published,
 "IsDeleted" AS is_deleted,"AttributesJson" AS attributes_json,"CreatedAt" AS created_at
 FROM "CommerceHubListings" WHERE "Id"='ff8cdce3-ad88-4467-b027-cdde5812e84d') l;
SELECT json_build_object('kind','source_variant','data',row_to_json(v))
FROM (SELECT "Id" AS id,"Price" AS price,"OnlinePrice" AS online_price,"IsActive" AS is_active
 FROM "ProductVariants" WHERE "Id"='81069782-7bda-4e35-bf24-3e13424f06f2') v;
COMMIT;
"""
sql_result = remote("kubectl exec -i -n shopixy-data shopixy-postgres-1 -c postgres -- "
                    "psql -X -qAt -v ON_ERROR_STOP=1 -U postgres -d app", sql)
result = {
    "verifiedAtUtc": datetime.datetime.now(datetime.timezone.utc).isoformat(), "readOnly": True,
    "workloads": workloads, "health": health, "configurationFiles": config,
    "environmentEnabledFlags": enabled_env, "adapterEnvironmentVariableNames": adapter_env_names,
    "environmentName": env.get("ASPNETCORE_ENVIRONMENT", env.get("DOTNET_ENVIRONMENT", "not specified")),
    "adapterCommandLineOverrideNames": config_override_arguments,
    "sql": [json.loads(line) for line in sql_result.splitlines() if line.startswith("{")],
}
save("runtime-" + phase + ".json", result)
print(json.dumps({"phase": phase, "workloads": [{"name": w["name"], "generation": w["generation"],
                                              "readyReplicas": w["readyReplicas"]} for w in workloads],
                  "health": health}, indent=2))
