"""Admission checks, then the unchanged, previously reviewed image-only runner."""
import hashlib
import json
import runpy
from pathlib import Path

root = Path.cwd()
inputs = json.loads((root / "release-inputs.json").read_text())
gate_path = root / "test-gates.json"
gates = json.loads(gate_path.read_text())
runtime = json.loads((root / "runtime-parent-compatibility.json").read_text())
if hashlib.sha256(gate_path.read_bytes()).hexdigest() != inputs["qualityGatesSha256"]:
    raise RuntimeError("Quality-gate receipt identity mismatch.")
if not gates["pass"] or not runtime["pass"] or not gates["builds"]["sourceUnchangedDuringBuild"]:
    raise RuntimeError("Unsuccessful release acceptance gate; refusing deployment.")
if gates["builds"].get("sourceCommit") != inputs["sourceCommit"]:
    raise RuntimeError("Build and release application revisions differ.")
if runtime["overlaySha256"] != inputs["components"]["api"]["artifactSha256"]:
    raise RuntimeError("Runtime compatibility evidence is for different application files.")
if gates["builds"]["apiArtifactSha256"] != inputs["components"]["api"]["artifactSha256"]:
    raise RuntimeError("API artifacts differ from the sealed build.")
if gates["builds"]["webIndexSha256"] != inputs["components"]["web"]["artifactSha256"]["index.html"]:
    raise RuntimeError("Web artifacts differ from the sealed build.")
if gates["builds"].get("webArtifactSha256") != inputs["components"]["web"]["artifactSha256"]:
    raise RuntimeError("Complete Web artifact manifest differs from the sealed build.")
runpy.run_path(str(root / "deploy-reviewed.py"), run_name="__main__")
