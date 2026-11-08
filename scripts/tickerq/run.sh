#!/usr/bin/env bash
set -euo pipefail

cd "$(dirname "$0")/../.."
umask 077
mkdir -p .local/tickerq/dotnet
export DOTNET_CLI_HOME="$PWD/.local/tickerq/dotnet"
export DOTNET_SKIP_FIRST_TIME_EXPERIENCE=1
export DOTNET_CLI_TELEMETRY_OPTOUT=1

flags=(-m:1 /nodeReuse:false /p:UseSharedCompilation=false --disable-build-servers)
if [[ "${1:-}" == test || "${1:-}" == test-f14 || "${1:-}" == verify-f14-migration ]]; then
    feed="$PWD/.local/tickerq-f14/feed"
    if [[ ! -f "$feed/W.TickerQ.Core.10.1.1-dameng.1.nupkg" ]]; then
        echo "Build the fenced TickerQ packages first with scripts/tickerq/build-f14.sh." >&2
        exit 1
    fi
    flags+=("/p:RestoreAdditionalProjectSources=$feed")
fi
case "${1:-}" in
  provision|provision-f14|provision-legacy)
    dotnet build scripts/tickerq/Provision.csproj --output .local/tickerq/provision "${flags[@]}" "${@:2}"
    lane=tickerq-f14
    if [[ $1 == provision-legacy ]]; then lane=tickerq; fi
    dotnet .local/tickerq/provision/Provision.dll "$PWD" "$lane"
    ;;
  test|test-f14)
    dotnet test tests/W.TickerQ.Dameng.Tests/W.TickerQ.Dameng.Tests.csproj "${flags[@]}" "${@:2}"
    ;;
  test-legacy)
    dotnet test tests/W.TickerQ.Dameng.Tests/W.TickerQ.Dameng.Tests.csproj "${flags[@]}" /p:TickerQF14=false "${@:2}"
    ;;
  seed-f14)
    dotnet test tests/W.TickerQ.Dameng.Tests/W.TickerQ.Dameng.Tests.csproj "${flags[@]}" /p:TickerQF14=false /p:TickerQF14Seed=true --filter 'FullyQualifiedName~Seed_published_legacy_data' "${@:2}"
    ;;
  verify-f14-migration)
    dotnet test tests/W.TickerQ.Dameng.Tests/W.TickerQ.Dameng.Tests.csproj "${flags[@]}" /p:TickerQF14=true /p:TickerQF14Migration=true --filter 'FullyQualifiedName~Migration_preserves_published_legacy_data' "${@:2}"
    ;;
  *)
    echo "Usage: scripts/tickerq/run.sh provision | test | provision-legacy | test-legacy | provision-f14 | seed-f14 | verify-f14-migration | test-f14 [MSBuild options]" >&2
    exit 2
    ;;
esac
