#!/usr/bin/env bash
set -euo pipefail

cd "$(dirname "$0")/../.."
umask 077
mkdir -p .local/tickerq/dotnet
export DOTNET_CLI_HOME="$PWD/.local/tickerq/dotnet"
export DOTNET_SKIP_FIRST_TIME_EXPERIENCE=1
export DOTNET_CLI_TELEMETRY_OPTOUT=1

flags=(-m:1 /nodeReuse:false /p:UseSharedCompilation=false --disable-build-servers)
case "${1:-}" in
  provision)
    dotnet build scripts/tickerq/Provision.csproj --output .local/tickerq/provision "${flags[@]}" "${@:2}"
    dotnet .local/tickerq/provision/Provision.dll "$PWD"
    ;;
  test)
    dotnet test tests/W.TickerQ.Dameng.Tests/W.TickerQ.Dameng.Tests.csproj "${flags[@]}" "${@:2}"
    ;;
  *)
    echo "Usage: scripts/tickerq/run.sh provision | test [MSBuild options]" >&2
    exit 2
    ;;
esac
