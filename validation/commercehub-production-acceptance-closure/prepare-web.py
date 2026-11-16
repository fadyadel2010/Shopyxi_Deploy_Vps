"""Seal only the changed Admin Web artifact against the accepted running parent."""
import hashlib
import json
import shutil
import subprocess
from pathlib import Path

ROOT = Path(__file__).resolve().parents[2]
COMMIT = "856648d0b61a7621c600524b5aa55cf4a72fb385"
BUILD = Path(r"C:\Users\fadya\AppData\Local\Temp\opencode\commercehub-closure-20261117-856648d")
PARENT = "shopixy/web:commercehub-publishing-4fb2beb-20261116"
PARENT_DIGEST = "sha256:e95bbdf5a645495003a3f88a580148238d738b6b1fd0ee345194d9e271de02a8"


def sha(data):
    return hashlib.sha256(data).hexdigest()


assert subprocess.check_output(["git", "rev-parse", "HEAD"], cwd=ROOT, text=True).strip() == COMMIT
assert not subprocess.check_output(["git", "diff", "--name-only", "HEAD"], cwd=ROOT)
assert not BUILD.exists(), "Do not overwrite an existing release context"
context = BUILD / "web-context"
context.mkdir(parents=True)
dist = ROOT / "PointOfSale.Web" / "dist"
shutil.copytree(dist, context / "dist")
files = [{"path": file.relative_to(dist).as_posix(), "sha256": sha(file.read_bytes())}
         for file in sorted(dist.rglob("*")) if file.is_file()]
dist_seal = sha(json.dumps(files, sort_keys=True, separators=(",", ":")).encode())
tree_seal = sha(subprocess.check_output(["git", "ls-tree", "-r", "--full-tree", COMMIT], cwd=ROOT))
(context / "Dockerfile").write_text(f"""FROM {PARENT}
LABEL shopixy.io/source-commit="{COMMIT}" shopixy.io/source-input-sha256="{tree_seal}" shopixy.io/web-dist-sha256="{dist_seal}" shopixy.io/release="commercehub-production-acceptance-closure-20261117"
USER 0
RUN rm -rf /usr/share/nginx/html/*
COPY dist/ /usr/share/nginx/html/
USER 101:101
""", encoding="utf-8", newline="\n")
result = {
    "sourceCommit": COMMIT, "sourceInputSha256": tree_seal, "parentAlias": PARENT,
    "parentDigest": PARENT_DIGEST, "webDistSha256": dist_seal, "files": files,
    "changedComponents": ["Admin Web"], "apiBuild": False, "storefrontBuild": False,
}
(BUILD / "artifact-inputs.json").write_text(json.dumps(result, indent=2) + "\n")
(Path(__file__).resolve().parent / "artifact-inputs.json").write_text(json.dumps(result, indent=2) + "\n")
print(json.dumps({k: v for k, v in result.items() if k != "files"}, indent=2))
