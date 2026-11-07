#!/usr/bin/env bash
set -euo pipefail
cd "$(dirname "$0")/../.."
mkdir -p .local/flexlabs-dotnet
export DOTNET_CLI_HOME="$PWD/.local/flexlabs-dotnet"
export DOTNET_SKIP_FIRST_TIME_EXPERIENCE=1 DOTNET_CLI_TELEMETRY_OPTOUT=1
export NUGET_PACKAGES="${NUGET_PACKAGES:-$HOME/.nuget/packages}"
flags=(-m:1 /nodeReuse:false /p:UseSharedCompilation=false --disable-build-servers)
case "${1:-}" in
  provision)
    shift
    project=scripts/flexlabs/Provision/Provision.csproj
    dotnet build "$project" "${flags[@]}" "$@"
    dotnet scripts/flexlabs/Provision/bin/Debug/net10.0/Provision.dll
    ;;
  test)
    shift
    export FLEXLABS_DM_ISOLATION="$PWD/.local/flexlabs-isolation.json"
    dotnet test tests/W.FlexLabs.Upsert.Dameng.Tests "${flags[@]}" "$@"
    ;;
  *)
    echo "Usage: scripts/flexlabs/run.sh provision | test" >&2
    exit 2
    ;;
esac
