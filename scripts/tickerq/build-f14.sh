#!/usr/bin/env bash
set -euo pipefail
cd "$(dirname "$0")/../.."
umask 077

work="$PWD/.local/tickerq-f14"
feed="$work/feed"
mkdir -p "$work/dotnet"
export DOTNET_CLI_HOME="$work/dotnet"
export DOTNET_SKIP_FIRST_TIME_EXPERIENCE=1
export DOTNET_CLI_TELEMETRY_OPTOUT=1
export NUGET_PACKAGES="${NUGET_PACKAGES:-$HOME/.nuget/packages}"
published_cache="$NUGET_PACKAGES"
mkdir -p "$published_cache"

upstream=b2684f41a1c22f09e05cbac087c117751bd3e78c
version=10.1.1-dameng.1
source=$(mktemp -d "$work/source.XXXXXXXX")
trap 'rm -rf "$source"' EXIT
staging="$source/feed"
mkdir "$staging"
git -C "$source" init --quiet
git -C "$source" fetch --quiet --depth=1 "${TICKERQ_UPSTREAM_REPO:-https://github.com/Arcenox-co/TickerQ.git}" "$upstream"
git -C "$source" checkout --quiet --detach "$upstream"
git -C "$source" apply "$PWD/scripts/tickerq/tickerq-10.1.1-f14.patch"

flags=(-m:1 /nodeReuse:false /p:UseSharedCompilation=false --disable-build-servers)
if [[ $# -eq 0 ]]; then
    set -- https://api.nuget.org/v3/index.json
fi
sources=(--source "$published_cache")
for source_arg in "$@"; do sources+=(--source "$source_arg"); done

# Preserve the released source generator; the patch changes no analyzer behavior.
dotnet restore src/W.TickerQ.Dameng/W.TickerQ.Dameng.csproj "${flags[@]}" "${sources[@]}" /p:TickerQF14=false
analyzer_dir="$source/src/TickerQ.SourceGenerator/bin/Release/netstandard2.0"
mkdir -p "$analyzer_dir"
cp "$published_cache/tickerq/10.1.1/analyzers/dotnet/cs/TickerQ.SourceGenerator.dll" "$analyzer_dir/"

# Exact patterns beat the wildcard on the published sources, including a pre-existing cache.
config="$source/nuget.config"
python3 - "$config" "$staging" "$published_cache" "$@" <<'PY'
import sys
from xml.etree.ElementTree import Element, ElementTree, SubElement

config, staging, *published = sys.argv[1:]
root = Element("configuration")
sources = SubElement(root, "packageSources")
SubElement(sources, "clear")
SubElement(sources, "add", key="fenced", value=staging)
mapping = SubElement(root, "packageSourceMapping")
patch = SubElement(mapping, "packageSource", key="fenced")
for name in ("Core", "EntityFrameworkCore", "Utilities"):
    SubElement(patch, "package", pattern=f"W.TickerQ.{name}")
for index, source in enumerate(published):
    key = f"published-{index}"
    SubElement(sources, "add", key=key, value=source)
    SubElement(SubElement(mapping, "packageSource", key=key), "package", pattern="*")
ElementTree(root).write(config, encoding="utf-8", xml_declaration=True)
PY

export NUGET_PACKAGES="$source/packages"
properties=(/p:Version="$version" /p:TargetFramework=net10.0 /p:DotNetVersion=10.0.7 /p:GeneratePackageOnBuild=false /p:ContinuousIntegrationBuild=true /p:PathMap="$source=/_/TickerQ")
for name in TickerQ.Utilities TickerQ TickerQ.EntityFrameworkCore; do
    project="$source/src/$name/$name.csproj"
    dotnet restore "$project" "${flags[@]}" "${properties[@]}" --configfile "$config"
    if [[ "$name" != TickerQ.Utilities ]]; then
        cached="$NUGET_PACKAGES/w.tickerq.utilities/$version/w.tickerq.utilities.$version.nupkg"
        cmp "$staging/W.TickerQ.Utilities.$version.nupkg" "$cached"
        echo "$name restored W.TickerQ.Utilities from this build."
    fi
    dotnet pack "$project" -c Release --no-restore --output "$staging" "${flags[@]}" "${properties[@]}"
done

# Publish a complete local feed only after all package builds and provenance checks succeed.
if [[ -e "$feed" ]]; then mv "$feed" "$source/previous-feed"; fi
if ! mv "$staging" "$feed"; then
    if [[ -e "$source/previous-feed" ]]; then mv "$source/previous-feed" "$feed"; fi
    exit 1
fi
(cd "$feed" && shasum -a 256 ./*.nupkg)
