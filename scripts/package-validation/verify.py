#!/usr/bin/env python3
"""Build and validate distributable packages without executing any database code."""
import argparse
import datetime
import hashlib
import json
import os
from pathlib import Path
import re
import shutil
import subprocess
import sys
import tempfile
import uuid
import xml.etree.ElementTree as ET
import zipfile

ROOT = Path(__file__).resolve().parents[2]
sys.path.insert(0, str(ROOT / "scripts"))
from source_build import PATCH_ASSEMBLIES, PATCH_VERSION, prepare_source, verify_source_cache, write_config

ADAPTER_IDS = (
    "W.FlexLabs.Upsert.Dameng", "W.Hangfire.Dameng",
    "W.OpenIddict.Dameng", "W.TickerQ.Dameng",
)
PACKAGE_IDS = (*ADAPTER_IDS, *PATCH_ASSEMBLIES)
PROVISIONERS = (
    "scripts/flexlabs/Provision/Provision.csproj",
    "scripts/hangfire/Provision.csproj",
    "scripts/identity/Provision/Provision.csproj",
    "scripts/namingconventions/Provision.csproj",
    "scripts/openiddict/Provision/Provision.csproj",
    "scripts/tickerq/Provision.csproj",
)
FLAGS = ["-m:1", "/nodeReuse:false", "/p:UseSharedCompilation=false", "--disable-build-servers"]
COMMON_DEPENDENCIES = {"W.DamengProvider": "0.3.1", "Hangfire.Core": "1.8.23"}
EF_DEPENDENCIES = {
    "Microsoft.EntityFrameworkCore": "10.0.12",
    "Microsoft.EntityFrameworkCore.Relational": "10.0.12",
    "W.EntityFrameworkCore.Dameng": "0.5.0",
    "W.EntityFrameworkCore.Dameng.WDriver": "0.5.0-preview.1",
    "FlexLabs.EntityFrameworkCore.Upsert": "10.1.0",
    **{name: PATCH_VERSION for name in PATCH_ASSEMBLIES},
    "OpenIddict.EntityFrameworkCore": "6.3.0",
}


class ValidationError(Exception):
    pass


def require(condition, message):
    if not condition:
        raise ValidationError(message)


def sha256(path):
    return hashlib.sha256(Path(path).read_bytes()).hexdigest()


def libraries(assets):
    result = {}
    for key, value in assets["libraries"].items():
        name, version = key.rsplit("/", 1)
        require(value["type"] == "package", f"Consumer contains a non-package reference: {name}")
        require(name not in result, f"Multiple resolved versions of {name}")
        result[name] = (version, value)
    return result


def check_dependencies(resolved, all_libraries):
    expected = dict(COMMON_DEPENDENCIES)
    if all_libraries:
        expected.update(EF_DEPENDENCIES)
    if all_libraries:
        require(not any(name in resolved for name in ("TickerQ", "TickerQ.Utilities", "TickerQ.EntityFrameworkCore")),
                "Unpatched TickerQ packages must not coexist with W.TickerQ packages")
    for name, version in expected.items():
        require(name in resolved and resolved[name][0] == version,
                f"Dependency contract mismatch: {name} must resolve to {version}")
    version = resolved.get("Newtonsoft.Json", ("",))[0]
    match = re.fullmatch(r"13\.(\d+)\.(\d+)", version)
    require(match is not None and (int(match[1]), int(match[2])) >= (0, 3),
            "Newtonsoft.Json must resolve to a stable 13.x version >= 13.0.3")


def check_consumer(project, cache, packages, all_libraries):
    assets = json.loads((project / "obj/project.assets.json").read_text())
    require(not project.resolve().is_relative_to(ROOT), "Consumer must be outside the repository")
    resolved = libraries(assets)
    expected_ids = PACKAGE_IDS if all_libraries else ("W.Hangfire.Dameng",)
    check_dependencies(resolved, all_libraries)
    result = {}
    for name in expected_ids:
        expected = packages[name]
        require(name in resolved and resolved[name][0] == expected["version"],
                f"Consumer did not resolve the freshly packed version of {name}")
        folder = cache / name.lower() / expected["version"].lower()
        require(not folder.is_symlink(), f"Local package cache must not be a symlink: {name}")
        nupkg = folder / f"{name.lower()}.{expected['version'].lower()}.nupkg"
        require(nupkg.is_file() and sha256(nupkg) == expected["sha256"],
                f"Consumer package differs from this run's artifact: {name}")
        copied = project / "bin/Release/net10.0" / f"{PATCH_ASSEMBLIES.get(name, name)}.dll"
        require(copied.is_file() and sha256(copied) == expected["dllSha256"],
                f"Consumer assembly differs from this run's build: {name}")
        result[name] = expected["version"]
    # An isolated Hangfire consumer must not accidentally acquire an EF/test graph.
    if not all_libraries:
        require(not any(name.startswith(("Microsoft.EntityFrameworkCore", "Microsoft.NET.Test.Sdk", "xunit"))
                        for name in resolved), "Hangfire-only consumer is contaminated by EF/test dependencies")
    return {"packages": result, "dependencies": {name: value[0] for name, value in resolved.items()}}


def inspect_packages(output):
    packages = {}
    for path in (output / "packages").glob("*.nupkg"):
        with zipfile.ZipFile(path) as archive:
            names = [name for name in archive.namelist() if name.endswith(".nuspec")]
            require(len(names) == 1, "Package must have exactly one nuspec")
            metadata = ET.fromstring(archive.read(names[0]))
            name = metadata.find(".//{*}id").text
            version = metadata.find(".//{*}version").text
            require(name in PACKAGE_IDS and name not in packages, f"Unexpected/duplicate package: {name}")
            require(re.fullmatch(r"[A-Za-z0-9.+-]+", version), "Invalid package version")
            dll = archive.read(f"lib/net10.0/{PATCH_ASSEMBLIES.get(name, name)}.dll")
            if name in PATCH_ASSEMBLIES:
                require(version == PATCH_VERSION, f"Wrong patched TickerQ version: {name}")
                require("LICENSE" in archive.namelist(), f"Missing upstream license: {name}")
                fresh = output / "patched-packages" / path.name
                require(fresh.is_file() and sha256(path) == sha256(fresh), f"Patch package differs from rebuild: {name}")
            else:
                built = ROOT / "src" / name / "bin/Release/net10.0" / f"{name}.dll"
                require(dll == built.read_bytes(), f"Packed DLL differs from build: {name}")
            require("README.md" in archive.namelist(), f"Missing package README: {name}")
            packages[name] = {"version": version, "sha256": sha256(path),
                              "dllSha256": hashlib.sha256(dll).hexdigest()}
    require(set(packages) == set(PACKAGE_IDS), "Expected four adapters and three freshly rebuilt TickerQ packages")
    return packages


def seed_external_dependencies(cache):
    """Reuse external package payloads, never local adapter slots or test dependency graphs."""
    cache.mkdir()
    local_ids = {name.lower() for name in PACKAGE_IDS}
    for name in ADAPTER_IDS:
        assets = json.loads((ROOT / "src" / name / "obj/project.assets.json").read_text())
        for key, library in assets["libraries"].items():
            package, version = key.rsplit("/", 1)
            if library["type"] != "package" or package.lower() in local_ids:
                continue
            target = cache / package.lower() / version.lower()
            if target.exists():
                continue
            for source_root in assets["packageFolders"]:
                source = Path(source_root) / library["path"]
                if source.is_dir():
                    target.parent.mkdir(exist_ok=True)
                    # Copy, not symlink: restoring this isolated cache cannot mutate the global cache.
                    shutil.copytree(source, target)
                    break


def create_consumer(stage, name, packages, feed, external_cache, downgrade=False, legacy_mix=False):
    project = stage / name
    project.mkdir()
    all_libraries = name == "AllLibraries" or legacy_mix
    ids = ADAPTER_IDS if all_libraries else ("W.Hangfire.Dameng",)
    xml = ET.Element("Project", Sdk="Microsoft.NET.Sdk")
    props = ET.SubElement(xml, "PropertyGroup")
    for key, value in {"TargetFramework": "net10.0", "Nullable": "enable", "ImplicitUsings": "enable",
                       "CopyLocalLockFileAssemblies": "true", "IsPackable": "false"}.items():
        ET.SubElement(props, key).text = value
    refs = ET.SubElement(xml, "ItemGroup")
    for package in ids:
        ET.SubElement(refs, "PackageReference", Include=package, Version=f"[{packages[package]['version']}]")
    if legacy_mix:
        ET.SubElement(refs, "PackageReference", Include="TickerQ", Version="[10.1.1]")
    if downgrade:
        ET.SubElement(refs, "PackageReference", Include="Newtonsoft.Json", Version="[11.0.1]")
    ET.indent(xml)
    ET.ElementTree(xml).write(project / f"{name}.csproj", encoding="unicode")
    template = "AllLibraries" if all_libraries else "HangfireOnly"
    shutil.copyfile(ROOT / f"scripts/package-validation/consumers/{template}.cs", project / "Consumer.cs")
    write_config(project / "NuGet.Config", feed, external_cache, PACKAGE_IDS)
    return project


def run(command, log, env, cwd, expected_failure=None):
    with log.open("w") as output:
        completed = subprocess.run(command, cwd=cwd, env=env, stdout=output, stderr=subprocess.STDOUT)
    if expected_failure is not None:
        text = log.read_text(errors="replace")
        require(completed.returncode != 0 and all(re.search(pattern, text) for pattern in expected_failure),
                f"Negative check did not fail for the intended reason; see {log}")
    else:
        require(completed.returncode == 0, f"Command failed; see {log}")


def consumer_command(project, cache):
    return ["dotnet", "build", str(next(project.glob("*.csproj"))), "-c", "Release", *FLAGS,
            f"-p:RestorePackagesPath={cache}", "-p:ImportDirectoryBuildProps=false",
            "-p:ImportDirectoryBuildTargets=false", f"-p:RestoreConfigFile={project / 'NuGet.Config'}"]


def main():
    parser = argparse.ArgumentParser(description=__doc__)
    parser.add_argument("--output", type=Path, help="New evidence directory (must not already exist)")
    parser.add_argument("--negative-checks", action="store_true", help="Also reject wrong feeds, dependency downgrades and unpatched TickerQ mixes")
    args = parser.parse_args()
    run_id = datetime.datetime.now(datetime.timezone.utc).strftime("%Y%m%dT%H%M%SZ") + "-" + uuid.uuid4().hex[:8]
    output = (args.output or ROOT / "artifacts/package-validation" / run_id).resolve()
    output.mkdir(parents=True, exist_ok=False)
    (output / "packages").mkdir()
    report = {"status": "running", "databaseExecuted": False, "sourceRoot": str(ROOT), "provisioners": [],
              "consumers": {}, "negativeChecks": []}
    try:
        with tempfile.TemporaryDirectory(prefix="dameng-package-consumers-") as temporary:
            stage = Path(temporary)
            shutil.copyfile(ROOT / "global.json", stage / "global.json")
            env = dict(os.environ, DOTNET_CLI_HOME=str(stage / "dotnet-home"),
                       DOTNET_SKIP_FIRST_TIME_EXPERIENCE="1", DOTNET_CLI_TELEMETRY_OPTOUT="1")
            env["NUGET_PACKAGES"] = os.environ.get("NUGET_PACKAGES", str(Path.home() / ".nuget/packages"))
            print(f"Evidence: {output}", flush=True)
            run([sys.executable, str(ROOT / "scripts/package-validation/test_verifier.py")],
                output / "policy-tests.log", env, ROOT)
            report["policyTests"] = "passed"
            print("Rebuilding patched TickerQ dependencies", flush=True)
            env, source_config, external_cache, patch_feed = prepare_source(ROOT, output, env, run)
            source_flags = [*FLAGS, f"-p:RestoreConfigFile={source_config}",
                            f"-p:RestorePackagesPath={env['NUGET_PACKAGES']}"]
            print("Building source solution (no tests executed)", flush=True)
            run(["dotnet", "build", str(ROOT / "W.Dameng.Integrations.slnx"), "-c", "Release", *source_flags],
                output / "solution.log", env, ROOT)
            verify_source_cache(env["NUGET_PACKAGES"], patch_feed)
            report["sourcePatchCacheVerified"] = True
            for index, relative in enumerate(PROVISIONERS):
                print(f"Building provisioner: {relative}", flush=True)
                run(["dotnet", "build", str(ROOT / relative), "-c", "Release", *source_flags],
                    output / f"provisioner-{index + 1}.log", env, ROOT)
                report["provisioners"].append(relative)
            for path in patch_feed.glob("*.nupkg"):
                shutil.copyfile(path, output / "packages" / path.name)
            for name in ADAPTER_IDS:
                run(["dotnet", "pack", str(ROOT / "src" / name / f"{name}.csproj"), "-c", "Release",
                     "--no-build", "--no-restore", "-o", str(output / "packages"), *FLAGS],
                    output / f"pack-{name}.log", env, ROOT)
            packages = inspect_packages(output)
            report["packages"] = packages
            cache = output / "consumer-cache"
            seed_external_dependencies(cache)
            # Ignore an inherited NUGET_PACKAGES override as well as any repository props/config.
            consumer_env = dict(env, NUGET_PACKAGES=str(cache))
            for name in ("AllLibraries", "HangfireOnly"):
                print(f"Building package-only consumer: {name}", flush=True)
                project = create_consumer(stage, name, packages, output / "packages", external_cache)
                run(consumer_command(project, cache), output / f"consumer-{name}.log", consumer_env, project)
                report["consumers"][name] = check_consumer(project, cache, packages, name == "AllLibraries")
                evidence = output / name
                evidence.mkdir()
                for file in ("Consumer.cs", f"{name}.csproj", "NuGet.Config", "obj/project.assets.json"):
                    shutil.copyfile(project / file, evidence / Path(file).name)
            if args.negative_checks:
                bad_cache = output / "wrong-feed-cache"
                seed_external_dependencies(bad_cache)
                empty_feed = output / "empty-feed"
                empty_feed.mkdir()
                project = create_consumer(stage, "WrongFeed", packages, empty_feed, external_cache)
                run(consumer_command(project, bad_cache), output / "negative-wrong-feed.log",
                    dict(env, NUGET_PACKAGES=str(bad_cache)), project,
                    expected_failure=[r"NU110[12]", r"W\.Hangfire\.Dameng"])
                report["negativeChecks"].append("wrong feed rejected by restore")
                project = create_consumer(stage, "Downgrade", packages, output / "packages", external_cache, downgrade=True)
                run(consumer_command(project, cache), output / "negative-downgrade.log", consumer_env, project,
                    expected_failure=[r"NU1605", r"Newtonsoft\.Json", r"13\.0\.3", r"11\.0\.1"])
                report["negativeChecks"].append("Newtonsoft.Json 11.0.1 downgrade rejected")
                project = create_consumer(stage, "LegacyMix", packages, output / "packages", external_cache, legacy_mix=True)
                run(consumer_command(project, cache), output / "negative-legacy-tickerq.log", consumer_env, project,
                    expected_failure=[r"W\.TickerQ\.Dameng requires the matching W\.TickerQ packages"])
                report["negativeChecks"].append("unpatched TickerQ assembly mix rejected")
            report["status"] = "passed"
            print("PASS: solution, six provisioners, seven packages and two independent consumers; no database execution.")
    except (ValidationError, OSError, ValueError, KeyError, zipfile.BadZipFile) as error:
        report["status"] = "failed"
        report["error"] = str(error)
        print(f"FAIL: {error}", file=sys.stderr)
    finally:
        (output / "report.json").write_text(json.dumps(report, indent=2) + "\n")
    return 0 if report["status"] == "passed" else 1


if __name__ == "__main__":
    sys.exit(main())
