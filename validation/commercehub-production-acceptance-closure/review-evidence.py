"""Validate closure claims and sanitized evidence before GitHub delivery."""
import hashlib
import json
import re
from pathlib import Path

ROOT = Path(__file__).resolve().parent
read = lambda name: json.loads((ROOT / name).read_text(encoding="utf-8-sig"))
baseline = read("runtime-before.json")
after = read("runtime-after.json")
deployment = read("deployment.json")
assert baseline["sql"] == after["sql"]
assert deployment["apiUnchanged"] and deployment["storefrontUnchanged"]
assert deployment["after"]["web"]["generation"] == 23
assert deployment["after"]["web"]["specSha256"] == deployment["expectedImageOnlySpecSha256"]
assert deployment["sourceCommit"] == "856648d0b61a7621c600524b5aa55cf4a72fb385"
for item in deployment["after"].values():
    assert item["readyReplicas"] == 1
    assert all(c["ready"] for pod in item["pods"] for c in pod["containers"])
assert after["health"]["/health/ready"]["httpStatus"] == 200
assert after["health"]["/health"]["httpStatus"] == 200
assert after["health"]["/health"]["status"] == "Degraded"
assert after["health"]["/health"]["checks"]["CommerceHubSearchSync"] == "Healthy"
assert set(after["health"]["/health"]["nonHealthyDescriptions"]) == {"certificate_provider", "caddy_edge"}
assert read("search-recovery.json")["attemptCount"] == 1
assert read("search-recovery.json")["outcome"] == "COMPLETED"
assert read("search-recovery.json")["protectedProductUnchanged"]
assert read("search-recovery.json")["listingUnchanged"]
assert read("search-final-readonly.json")["requests"][0]["count"] == 1
assert read("typecheck-final.json")["current"] == []
tests = read("frontend-tests.json")
assert tests["numPassedTests"] == 33 and tests["numFailedTests"] == 0 and tests["success"]
rules = baseline["sql"][0]["rules"]
assert len(rules) == 1
rule = rules[0]
assert rule["id"] == "98eca7ca-414a-442f-bdf6-fb320cfb4de4" and rule["rate"] == 0.1
assert rule["scope_id"] is None and rule["scope_type"] == 0 and rule["priority"] == 0
assert rule["is_active"] and not rule["is_deleted"]
for language, direction in (("ar", "rtl"), ("en", "ltr")):
    browser = read(f"browser-after-{language}.json")
    assert not browser["errors"] and not browser["failed"] and not browser["businessWrites"]
    assert browser["rendered"]["direction"] == direction
    assert browser["integrity"]["listingUnchanged"] and not browser["integrity"]["changedProtectedFields"]
    assert browser["integrity"]["posPrice"] == browser["integrity"]["effectiveStorefrontPrice"] == 10
    assert browser["integrity"]["listing"]["commerceHubPrice"] == 12
    assert browser["integrity"]["listing"]["priceOverrideMutationVersion"] == 1
    for mode, price, deduction, net in (("source", 10, 1, 9), ("custom", 12, 1.2, 10.8)):
        receipt = browser["modes"][mode]
        response = receipt["apiResponse"]["body"]
        assert response["sourceProductPrice"] == 10 and response["marketplacePrice"] == price
        assert response["rate"] == 0.1 and response["commissionAmount"] == deduction and response["merchantNet"] == net
        assert response["ruleId"] == rule["id"] and not response["usedLegacyFallback"]
        assert receipt["ui"]["completed"] and not receipt["ui"]["loading"] and not receipt["ui"]["horizontalOverflow"]
        assert receipt["ui"]["selectedMode"] == mode and len(receipt["ui"]["numericRows"]) == 5
        assert (ROOT / receipt["screenshot"]).read_bytes().startswith(b"\x89PNG\r\n\x1a\n")
arabic = read("browser-after-ar.json")
for leakage in ("Kitchen", "out_of_stock", "Help", "Soon", "Owner", "commerceHub.publishing.", "common.help"):
    assert leakage not in arabic["rendered"]["mainText"]
    assert leakage not in "\n".join(arabic["rendered"]["visibleLatinText"])
assert arabic["rendered"]["roleText"] == "مالك"
assert arabic["rendered"]["helpText"] == "مساعدة\nقريباً"
assert read("running-artifact-parity.json")["webFilesCompared"] == 70
assert read("running-artifact-parity.json")["matchesAcceptedApiBuild"]
# Credential VALUES, not harmless key names or source code describing cookie handling.
patterns = {
    "jwt": r"\beyJ[A-Za-z0-9_-]{10,}\.[A-Za-z0-9_-]{10,}\.[A-Za-z0-9_-]{10,}",
    "github-token": r"\b(?:gh[pousr]_[A-Za-z0-9]{20,}|github_pat_[A-Za-z0-9_]{20,})",
    "private-key": r"-----BEGIN (?:RSA |EC |OPENSSH )?PRIVATE KEY-----",
    "credential-url": r"https?://[^/\s:@]+:[^/\s@]+@",
    "bearer-value": r"(?i)\bBearer\s+[A-Za-z0-9._~-]{12,}",
    "email": r"\b[A-Za-z0-9._%+-]+@[A-Za-z0-9.-]+\.[A-Za-z]{2,}\b",
}
files = [p for p in ROOT.iterdir() if p.is_file() and p.suffix != ".log"]
findings = []
for file in files:
    if file.suffix in (".json", ".md", ".py", ".cjs", ".sh"):
        text = file.read_text(encoding="utf-8-sig")
        assert "\ufffd" not in text
        for kind, pattern in patterns.items():
            if re.search(pattern, text):
                findings.append({"file": file.name, "kind": kind})
assert not findings, findings
report = (ROOT / "FINAL-REPORT.md").read_text(encoding="utf-8")
for filename in re.findall(r"`([^`\n/]+\.(?:json|png))`", report):
    if filename == "github-delivery.json" and not (ROOT / filename).exists():
        continue
    assert (ROOT / filename).exists(), filename
result = {"evidenceAssertions": "PASS", "sensitivePatternFindings": findings,
          "screenshots": len(list(ROOT.glob("*.png"))), "testsPassed": 33,
          "aggregateStatus": "Degraded", "requiredBusinessHealth": "Healthy",
          "applicationAcceptance": "FIXED AND RE-TESTED"}
(ROOT / "evidence-review.json").write_text(json.dumps(result, indent=2) + "\n")
print(json.dumps(result, indent=2))
