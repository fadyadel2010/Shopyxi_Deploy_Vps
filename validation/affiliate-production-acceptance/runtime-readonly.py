"""Run over existing SSH alias stdin. Only Affiliate-path workloads; no secrets exported."""
import datetime
import json
import subprocess


def command(args):
    result = subprocess.run(args, capture_output=True, text=True, timeout=45)
    if result.returncode:
        raise RuntimeError("Read-only runtime inspection failed")
    return result.stdout


namespace = "shopixy-app"
workloads = []
for name in ("shopixy-api", "shopixy-web", "shopixy-storefront"):
    deployment = json.loads(command(["/usr/local/bin/kubectl", "get", "deployment", name, "-n", namespace, "-o", "json"]))
    containers = deployment["spec"]["template"]["spec"]["containers"]
    workloads.append({"name": name, "generation": deployment["metadata"]["generation"],
        "observedGeneration": deployment.get("status", {}).get("observedGeneration"),
        "desired": deployment["spec"].get("replicas"), "ready": deployment.get("status", {}).get("readyReplicas", 0),
        "available": deployment.get("status", {}).get("availableReplicas", 0),
        "images": [{"name": c["name"], "image": c["image"]} for c in containers]})
    if name == "shopixy-api":
        api = next(c for c in containers if c["name"] == "api")
        allow = {"ASPNETCORE_ENVIRONMENT", "Smtp__Enabled", "SyntheticQa__Enabled", "AffiliateQaPayout__Enabled"}
        workloads[-1]["explicitSafeFlags"] = [{"name": e["name"], "value": e.get("value", "<reference not exported>")}
            for e in api.get("env", []) if e["name"] in allow]
        # Select only an allowlist, inside the process; never print the rest of the environment.
        script = 'import os,json; names=["ASPNETCORE_ENVIRONMENT","Smtp__Enabled","SyntheticQa__Enabled","AffiliateQaPayout__Enabled"]; print(json.dumps({k:os.environ.get(k,"<not explicitly set>") for k in names}))'
        try:
            workloads[-1]["processSafeFlags"] = json.loads(command(["/usr/local/bin/kubectl", "exec", "deployment/shopixy-api", "-n", namespace,
                "-c", "api", "--", "python3", "-c", script]))
        except Exception:
            workloads[-1]["processSafeFlags"] = {"status": "Unavailable; no configuration inferred"}
        status = json.loads(command(["/usr/local/bin/k3s", "crictl", "inspecti", api["image"]]))["status"]
        workloads[-1]["runtimeIdentity"] = {"id": status["id"], "repoDigests": status.get("repoDigests", [])}

print(json.dumps({"verifiedAtUtc": datetime.datetime.now(datetime.timezone.utc).isoformat(), "namespace": namespace,
    "method": "Read-only kubectl GET; allowlisted process flags only; CRI image inspection. No rollout, no secrets, no business writes.",
    "workloads": workloads}, indent=2))
