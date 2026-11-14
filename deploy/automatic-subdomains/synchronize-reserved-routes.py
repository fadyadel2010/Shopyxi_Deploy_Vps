"""Render bounded wildcard exclusions from the canonical backend registry."""
import pathlib
import re

root = pathlib.Path(__file__).resolve().parents[2]
authority = (root / "PointOfSale.Core/Tenancy/StoreSlug.cs").read_text(encoding="utf-8-sig")
block = re.search(r"ReservedValues\s*=.*?\{(.*?)\};", authority, re.S)
assert block, "Canonical reserved registry not found"
labels = sorted(set(re.findall(r'"([a-z0-9-]+)"', block[1])))
assert len(labels) > 50
exclude = " && !HostRegexp(`^(" + "|".join(labels) + r")\.shopyxi\.com$`)"
path = pathlib.Path(__file__).with_name("05-traefik-wildcard-routes.yaml")
text = path.read_text(encoding="utf-8-sig")
text, count = re.subn(r"(match: HostRegexp\(`\^\[a-z0-9\].*?`\))(?: && !HostRegexp\(`[^`]+`\))?", lambda m: m[1] + exclude, text)
assert count == 4, count
path.write_text(text, encoding="utf-8", newline="\n")
print(f"Synchronized {len(labels)} canonical exclusions on {count} wildcard rules")
