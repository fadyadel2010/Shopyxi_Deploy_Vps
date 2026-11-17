"""Verify the three-assembly API overlay can bind against the running parent image.

The parent container owns deps.json, runtimeconfig.json and every dependency
assembly.  The overlay replaces exactly PointOfSale.API.dll,
PointOfSale.Core.dll and PointOfSale.Infrastructure.dll, so the only thing that
can break at runtime is an unresolved assembly reference (or an app-local
reference whose required version is newer than the parent provides).

This gate reads the assembly reference tables with System.Reflection.Metadata
and resolves each reference against the parent image's /app folder and its
installed .NET shared frameworks.  Design-time packages that appear only in
the Release deps.json (EF Design, Roslyn, ...) never enter an assembly
reference table because they are PrivateAssets=all with IncludeAssets
excluding compile/runtime, so they are reported separately and are not part of
the pass/fail decision.
"""
import hashlib
import json
import os
import shutil
import subprocess
import sys
import tempfile
from pathlib import Path

SOURCE = Path.cwd()
ROOT = Path(os.environ.get(
    "FINANCE_RELEASE_ROOT",
    "/mnt/c/Users/fadya/AppData/Local/Temp/opencode/finance-release-20261117",
))
INPUTS = json.loads((ROOT / "release-inputs.json").read_text())
API = INPUTS["components"]["api"]
PARENT_IMAGE = API["parentImage"]
OVERLAY = ROOT / "api-context"
RESULT_PATH = SOURCE / "validation/super-admin-finance/runtime-parent-compatibility.json"

INSPECTOR_SOURCE = r'''
using System.Reflection.Metadata;
using System.Reflection.PortableExecutable;
using System.Text.Json;

var results = new List<object>();
foreach (var path in args)
{
    using var fs = File.OpenRead(path);
    using var pe = new PEReader(fs);
    var md = pe.GetMetadataReader();
    var def = md.GetAssemblyDefinition();
    var self = new { name = md.GetString(def.Name), version = def.Version.ToString() };
    var refs = new List<object>();
    foreach (var handle in md.AssemblyReferences)
    {
        var r = md.GetAssemblyReference(handle);
        refs.Add(new { name = md.GetString(r.Name), version = r.Version.ToString() });
    }
    results.Add(new { file = Path.GetFileName(path), self, references = refs });
}
Console.WriteLine(JsonSerializer.Serialize(results, new JsonSerializerOptions { WriteIndented = true }));
'''


def run(cmd, **kwargs):
    return subprocess.run(cmd, check=True, text=True, **kwargs)


def sha256(path):
    digest = hashlib.sha256()
    with open(path, "rb") as handle:
        for chunk in iter(lambda: handle.read(1024 * 1024), b""):
            digest.update(chunk)
    return digest.hexdigest()


def parse_version(text):
    return tuple(int(part) for part in text.split("."))


def main():
    overlay_files = sorted(OVERLAY.glob("*.dll"))
    overlay_hashes = {path.name: sha256(path) for path in overlay_files}
    expected = API["artifactSha256"]
    if overlay_hashes != expected:
        raise RuntimeError(f"Overlay artifacts do not match release inputs: {overlay_hashes} != {expected}")
    if len(overlay_files) != 3:
        raise RuntimeError(f"Expected exactly three overlay assemblies, found {[p.name for p in overlay_files]}")

    work = Path(tempfile.mkdtemp(prefix="runtime-parent-", dir=ROOT))
    try:
        parent_config_digest = run(
            ["podman", "image", "inspect", "--format", "{{.Id}}", PARENT_IMAGE],
            capture_output=True,
        ).stdout.strip()
        if not parent_config_digest.startswith("sha256:"):
            parent_config_digest = f"sha256:{parent_config_digest}"
        if parent_config_digest != API["parentConfigDigest"]:
            raise RuntimeError(
                f"Parent image config digest {parent_config_digest} != {API['parentConfigDigest']}")
        container = run(["podman", "create", PARENT_IMAGE], capture_output=True).stdout.strip()
        try:
            (work / "app").mkdir()
            run(["podman", "cp", f"{container}:/app/.", str(work / "app")])
            connection = run(
                ["podman", "run", "--rm", "--entrypoint", "sh", PARENT_IMAGE, "-c",
                 "ls /usr/share/dotnet/shared/*/*/*.dll"],
                capture_output=True,
            ).stdout
        finally:
            run(["podman", "rm", "-f", container], capture_output=True)
        runtimeconfig = json.loads((work / "app/PointOfSale.API.runtimeconfig.json").read_text())

        inspector = work / "asmrefs"
        inspector.mkdir()
        (inspector / "asmrefs.csproj").write_text(
            '<Project Sdk="Microsoft.NET.Sdk"><PropertyGroup><OutputType>Exe</OutputType>'
            '<TargetFramework>net9.0</TargetFramework><Nullable>enable</Nullable>'
            '<ImplicitUsings>enable</ImplicitUsings><InvariantGlobalization>true</InvariantGlobalization>'
            '</PropertyGroup></Project>'
        )
        (inspector / "Program.cs").write_text(INSPECTOR_SOURCE)
        run(["dotnet", "build", "-v", "q", "--nologo"], cwd=inspector, capture_output=True)
        inspector_dll = inspector / "bin/Debug/net9.0/asmrefs.dll"

        def inspect_assemblies(paths):
            return json.loads(run(["dotnet", str(inspector_dll), *map(str, paths)], capture_output=True).stdout)

        new_refs = inspect_assemblies(overlay_files)
        parent_app = sorted((work / "app").glob("*.dll"))
        parent_refs = inspect_assemblies(parent_app)

        app_names = {path.stem for path in parent_app}
        framework_names = {Path(line.strip()).stem for line in connection.splitlines() if line.strip()}
        parent_versions = {item["self"]["name"]: item["self"]["version"] for item in parent_refs}

        missing = {}
        version_raises = []
        used_from_app = set()
        used_from_framework = set()
        for item in new_refs:
            for ref in item["references"]:
                name = ref["name"]
                if name in app_names:
                    used_from_app.add(name)
                    parent_version = parent_versions.get(name)
                    if parent_version and parse_version(ref["version"]) > parse_version(parent_version):
                        version_raises.append({
                            "assembly": item["file"], "reference": name,
                            "required": ref["version"], "parent": parent_version,
                        })
                elif name in framework_names:
                    used_from_framework.add(name)
                else:
                    missing.setdefault(item["file"], []).append(name)

        parent_overlay_refs = {item["file"]: item for item in parent_refs
                               if item["file"] in {path.name for path in overlay_files}}
        added_refs = {
            item["file"]: sorted({ref["name"] for ref in item["references"]} -
                                 {ref["name"] for ref in parent_overlay_refs.get(item["file"], {"references": []})["references"]})
            for item in new_refs
        }
        added_refs = {name: refs for name, refs in added_refs.items() if refs}

        parent_deps = json.loads((work / "app/PointOfSale.API.deps.json").read_text())
        new_deps = json.loads(
            (SOURCE / "PointOfSale.API/bin/Release/net10.0/PointOfSale.API.deps.json").read_text())
        def packages(deps):
            return {name: meta.get("version") for name, meta in deps["libraries"].items() if meta["type"] == "package"}
        old_packages, new_packages = packages(parent_deps), packages(new_deps)
        design_time_only = sorted(set(new_packages) - set(old_packages))

        parent_tfm = runtimeconfig["runtimeOptions"]["tfm"]
        source_tfm_raw = new_deps["runtimeTarget"]["name"].split("/")[0]

        def normalize_tfm(tfm):
            if tfm.lower().startswith(".netcoreapp,version=v"):
                return "net" + tfm.split("=v", 1)[1]
            return tfm.lower()

        tfm_match = normalize_tfm(parent_tfm) == normalize_tfm(source_tfm_raw) == "net10.0"

        common_version_diffs = {
            name: {"parent": old_packages[name], "source": new_packages[name]}
            for name in sorted(set(old_packages) & set(new_packages))
            if old_packages[name] != new_packages[name]
        }

        result = {
            "parentImage": PARENT_IMAGE,
            "parentImageId": parent_config_digest,
            "parentConfigDigest": parent_config_digest,
            "parentRuntimeTfm": parent_tfm,
            "sourceRuntimeTfm": source_tfm_raw,
            "sourceRuntimeTfmNormalized": normalize_tfm(source_tfm_raw),
            "sourceRuntimeTfmIncludesRid": "/" in new_deps["runtimeTarget"]["name"],
            "tfmMatch": tfm_match,
            "overlayFileCount": len(overlay_files),
            "overlaySha256": overlay_hashes,
            "depsJsonReplaced": False,
            "runtimeconfigReplaced": False,
            "parentAppAssemblyCount": len(parent_app),
            "frameworkAssemblyCount": len(framework_names),
            "referencedAssemblyCount": len({ref["name"] for item in new_refs for ref in item["references"]}),
            "resolvedFromParentApp": sorted(used_from_app),
            "resolvedFromSharedFramework": sorted(used_from_framework),
            "missingReferences": missing,
            "appLocalVersionRaises": version_raises,
            "newReferencesNotInParentOverlay": added_refs,
            "commonRuntimePackageMetadataDifferences": common_version_diffs,
            "designTimeOnlyPackagesNotInParentDeps": design_time_only,
            "designTimePackagesExplanation": (
                "PrivateAssets=all with IncludeAssets excluding compile/runtime; they cannot appear in an "
                "assembly reference table and are not copied into the overlay."
            ),
            "pass": tfm_match and not missing and not version_raises and not common_version_diffs,
        }
        RESULT_PATH.write_text(json.dumps(result, indent=2) + "\n")
        print(json.dumps({key: result[key] for key in (
            "parentImage", "parentRuntimeTfm", "sourceRuntimeTfm", "tfmMatch", "overlayFileCount",
            "referencedAssemblyCount", "missingReferences", "appLocalVersionRaises",
            "commonRuntimePackageMetadataDifferences", "pass")}, indent=2))
        if not result["pass"]:
            raise RuntimeError("Parent runtime contract mismatch; refusing partial assembly overlay.")
    finally:
        shutil.rmtree(work, ignore_errors=True)


if __name__ == "__main__":
    sys.exit(main())
