#!/usr/bin/env bash
set -euo pipefail
cd "$(dirname "$0")/.."
mkdir -p .local/build-dotnet
export DOTNET_CLI_HOME="$PWD/.local/build-dotnet"
export DOTNET_SKIP_FIRST_TIME_EXPERIENCE=1
export DOTNET_CLI_TELEMETRY_OPTOUT=1
exec python3 scripts/source_build.py "$@"
