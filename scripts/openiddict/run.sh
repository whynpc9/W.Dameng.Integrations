#!/usr/bin/env bash
set -euo pipefail
cd "$(dirname "$0")/../.."
umask 077
mkdir -p .local/openiddict/dotnet-home
export DOTNET_CLI_HOME="$PWD/.local/openiddict/dotnet-home"
export DOTNET_SKIP_FIRST_TIME_EXPERIENCE=1 DOTNET_CLI_TELEMETRY_OPTOUT=1
export NUGET_PACKAGES="${NUGET_PACKAGES:-$HOME/.nuget/packages}"
export OPENIDDICT_DAMENG_TEST_STATE="$PWD/.local/openiddict/schema.json"
flags=(-m:1 /nodeReuse:false /p:UseSharedCompilation=false --disable-build-servers)
case "${1:-}" in
  provision)
    project=scripts/openiddict/Provision/Provision.csproj
    dotnet build "$project" "${flags[@]}"
    dotnet scripts/openiddict/Provision/bin/Debug/net10.0/Provision.dll \
      "${2:-/Users/wanghongyi/Projects/dameng-entityframework-core/.local-test.secrets.json}"
    ;;
  test)
    shift
    dotnet test tests/W.OpenIddict.Dameng.Tests/W.OpenIddict.Dameng.Tests.csproj \
      -c Release "${flags[@]}" "$@"
    ;;
  *)
    printf 'Usage: %s provision [admin-secrets-file] | test [test-options]\n' "$0" >&2
    exit 2
    ;;
esac
