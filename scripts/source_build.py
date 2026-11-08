#!/usr/bin/env python3
"""Build against freshly rebuilt TickerQ packages without changing the global cache."""
import datetime
import hashlib
import os
from pathlib import Path
import shutil
import subprocess
import sys
import uuid
import xml.etree.ElementTree as ET


PATCH_VERSION = "10.1.1-dameng.1"
PATCH_ASSEMBLIES = {
    "W.TickerQ.Core": "TickerQ",
    "W.TickerQ.Utilities": "TickerQ.Utilities",
    "W.TickerQ.EntityFrameworkCore": "TickerQ.EntityFrameworkCore",
}
FLAGS = ["-m:1", "/nodeReuse:false", "/p:UseSharedCompilation=false", "--disable-build-servers"]


def write_config(path, feed, external_cache, local_ids):
    config = ET.Element("configuration")
    sources = ET.SubElement(config, "packageSources")
    ET.SubElement(sources, "clear")
    for key, value in (("this-run", feed), ("external-cache", external_cache),
                       ("nuget.org", "https://api.nuget.org/v3/index.json")):
        ET.SubElement(sources, "add", key=key, value=str(value))
    mappings = ET.SubElement(config, "packageSourceMapping")
    local = ET.SubElement(mappings, "packageSource", key="this-run")
    for package in local_ids:
        ET.SubElement(local, "package", pattern=package)
    for key in ("external-cache", "nuget.org"):
        source = ET.SubElement(mappings, "packageSource", key=key)
        ET.SubElement(source, "package", pattern="*")
    ET.indent(config)
    ET.ElementTree(config).write(path, encoding="unicode")


def prepare_source(root, output, env, execute):
    external_cache = Path(env.get("NUGET_PACKAGES", str(Path.home() / ".nuget/packages"))).resolve()
    build_env = dict(env, NUGET_PACKAGES=str(external_cache))
    execute([str(root / "scripts/tickerq/build-f14.sh")], output / "tickerq-patch-build.log", build_env, root)
    feed = output / "patched-packages"
    feed.mkdir()
    for name in PATCH_ASSEMBLIES:
        filename = f"{name}.{PATCH_VERSION}.nupkg"
        shutil.copyfile(root / ".local/tickerq-f14/feed" / filename, feed / filename)
    config = output / "Source.NuGet.Config"
    write_config(config, feed, external_cache, PATCH_ASSEMBLIES)
    # A fresh restore cache and exact source mapping prevent same-version stale
    # patch packages in a global or earlier task cache from hiding source edits.
    cache = output / "source-cache"
    cache.mkdir()
    return dict(env, NUGET_PACKAGES=str(cache)), config, external_cache, feed


def verify_source_cache(cache, feed):
    for name in PATCH_ASSEMBLIES:
        fresh = feed / f"{name}.{PATCH_VERSION}.nupkg"
        cached = Path(cache) / name.lower() / PATCH_VERSION / f"{name.lower()}.{PATCH_VERSION}.nupkg"
        if hashlib.sha256(fresh.read_bytes()).digest() != hashlib.sha256(cached.read_bytes()).digest():
            raise ValueError(f"Source build used a stale TickerQ package: {name}")


def main():
    root = Path(__file__).resolve().parents[1]
    run_id = datetime.datetime.now(datetime.timezone.utc).strftime("%Y%m%dT%H%M%SZ") + "-" + uuid.uuid4().hex[:8]
    output = root / ".local/builds" / run_id
    output.mkdir(parents=True)
    env = dict(os.environ, DOTNET_CLI_HOME=str(output / "dotnet-home"),
               DOTNET_SKIP_FIRST_TIME_EXPERIENCE="1", DOTNET_CLI_TELEMETRY_OPTOUT="1")

    def execute(command, log, environment, cwd):
        with log.open("w") as stream:
            result = subprocess.run(command, cwd=cwd, env=environment, stdout=stream, stderr=subprocess.STDOUT)
        if result.returncode:
            raise RuntimeError(f"Build failed; see {log}")

    try:
        print(f"Build evidence: {output}", flush=True)
        env, config, _, feed = prepare_source(root, output, env, execute)
        execute(["dotnet", "build", str(root / "W.Dameng.Integrations.slnx"), "-c", "Release", *FLAGS,
                 *sys.argv[1:], f"-p:RestoreConfigFile={config}", f"-p:RestorePackagesPath={env['NUGET_PACKAGES']}"],
                output / "solution.log", env, root)
        verify_source_cache(env["NUGET_PACKAGES"], feed)
        print("PASS: source solution built against this run's patched TickerQ packages.")
        return 0
    except (OSError, ValueError, RuntimeError) as error:
        print(str(error), file=sys.stderr)
        return 1


if __name__ == "__main__":
    sys.exit(main())
