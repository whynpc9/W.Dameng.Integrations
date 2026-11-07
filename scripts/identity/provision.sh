#!/usr/bin/env bash
source "$(dirname "${BASH_SOURCE[0]}")/env.sh"
dotnet build scripts/identity/Provision "${identity_build_flags[@]}"
dotnet scripts/identity/Provision/bin/Debug/net10.0/Provision.dll
