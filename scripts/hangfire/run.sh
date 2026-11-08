#!/usr/bin/env bash
set -euo pipefail
cd "$(dirname "$0")/../.."
umask 077
mkdir -p .local/hangfire/dotnet
export DOTNET_CLI_HOME="$PWD/.local/hangfire/dotnet"
export DOTNET_SKIP_FIRST_TIME_EXPERIENCE=1 DOTNET_CLI_TELEMETRY_OPTOUT=1
run_name="${WHF_TEST_RUN:-}"
if [[ -n "$run_name" ]]; then
  [[ "$run_name" =~ ^[a-zA-Z0-9_-]+$ ]] || { echo 'Invalid Hangfire test run name.' >&2; exit 2; }
  mkdir -p ".local/hangfire/runs/$run_name"
  export WHF_TEST_SETTINGS="$PWD/.local/hangfire/runs/$run_name/database.secrets.json"
else
  export WHF_TEST_SETTINGS="$PWD/.local/hangfire/database.secrets.json"
fi
flags=(-m:1 /nodeReuse:false /p:UseSharedCompilation=false --disable-build-servers)
case "${1:-}" in
  provision)
    dotnet build scripts/hangfire/Provision.csproj -c Release "${flags[@]}"
    dotnet scripts/hangfire/bin/Release/net10.0/Provision.dll
    ;;
  test)
    shift
    dotnet test tests/W.Hangfire.Dameng.Tests/W.Hangfire.Dameng.Tests.csproj -c Release "${flags[@]}" "$@"
    ;;
  *) echo 'Usage: scripts/hangfire/run.sh provision | test [test options]' >&2; exit 2 ;;
esac
