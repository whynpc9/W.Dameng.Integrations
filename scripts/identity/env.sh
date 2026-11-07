#!/usr/bin/env bash
set -euo pipefail
identity_root=$(cd "$(dirname "${BASH_SOURCE[0]}")/../.." && pwd)
cd "$identity_root"
mkdir -p .local/dotnet
export DOTNET_CLI_HOME="$identity_root/.local/dotnet"
export DOTNET_SKIP_FIRST_TIME_EXPERIENCE=1
export DOTNET_CLI_TELEMETRY_OPTOUT=1
export W_DAMENG_IDENTITY_STATE="$identity_root/.local/identity.json"
identity_build_flags=(-m:1 /nodeReuse:false /p:UseSharedCompilation=false --disable-build-servers)
