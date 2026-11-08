#!/usr/bin/env bash
set -euo pipefail
umask 077
cd "$(dirname "$0")/../.."
root="$PWD"
local_dir="$root/.local/namingconventions"
mkdir -p "$local_dir/dotnet"
export DOTNET_CLI_HOME="$local_dir/dotnet"
export DOTNET_SKIP_FIRST_TIME_EXPERIENCE=1
export DOTNET_CLI_TELEMETRY_OPTOUT=1
export NAMING_TEST_SETTINGS="$local_dir/owned.json"

usage() {
  printf 'Usage: %s provision\n       %s test [dotnet test arguments...]\n' "$0" "$0" >&2
  exit 2
}

command="${1:-}"
case "$command" in
  provision) [[ "$#" -eq 1 ]] || usage ;;
  test) ;;
  *) usage ;;
esac
shift

lock="$local_dir/run.lock"
mkdir "$lock" || { echo "Naming conventions provision/test lock is already held." >&2; exit 1; }
trap 'rmdir "$lock"' EXIT
flags=(-m:1 /nodeReuse:false /p:UseSharedCompilation=false --disable-build-servers)
if [[ "$command" == provision ]]; then
  project="$root/scripts/namingconventions/Provision.csproj"
  dotnet build "$project" "${flags[@]}"
  dotnet "$root/scripts/namingconventions/bin/Debug/net10.0/Provision.dll"
else
  dotnet test "$root/tests/W.Dameng.NamingConventions.Tests" "${flags[@]}" -p:RestoreLockedMode=true "$@"
fi
